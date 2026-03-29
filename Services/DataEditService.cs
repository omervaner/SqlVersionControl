using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class DataEditService
{
    private readonly DatabaseService _db;

    public DataEditService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// Detect if SQL is a simple single-table SELECT (eligible for edit mode).
    /// Returns schema and table name, or nulls if not a simple SELECT.
    /// </summary>
    public static (string? Schema, string? Table) ParseSimpleSelect(string sql)
    {
        // Strip leading line comments (-- ...) and block comments (/* ... */)
        var normalized = Regex.Replace(sql, @"(--[^\r\n]*[\r\n]*|/\*[\s\S]*?\*/\s*)", "").Trim().TrimEnd(';');

        if (!Regex.IsMatch(normalized, @"^\s*SELECT\b", RegexOptions.IgnoreCase))
            return (null, null);

        // Disqualifiers: JOINs, UNIONs, subqueries
        if (Regex.IsMatch(normalized, @"\b(JOIN|UNION|EXCEPT|INTERSECT)\b", RegexOptions.IgnoreCase))
            return (null, null);

        if (Regex.IsMatch(normalized, @"\(\s*SELECT\b", RegexOptions.IgnoreCase))
            return (null, null);

        // Extract table reference from FROM clause
        var match = Regex.Match(normalized,
            @"\bFROM\s+((?:\[?\w+\]?\.)*\[?\w+\]?)",
            RegexOptions.IgnoreCase);

        if (!match.Success) return (null, null);

        var tableRef = match.Groups[1].Value;
        var parts = tableRef.Split('.')
            .Select(p => p.Trim('[', ']'))
            .ToArray();

        return parts.Length switch
        {
            1 => ("dbo", parts[0]),
            2 => (parts[0], parts[1]),
            3 => (parts[1], parts[2]), // db.schema.table → ignore db
            _ => (null, null)
        };
    }

    /// <summary>
    /// Get primary key column names for a table.
    /// </summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(string database, string schema, string table)
        => await GetPrimaryKeyColumnsCoreAsync(_db.GetConnectionStringForDatabase(database), schema, table);

    /// <summary>
    /// Per-tab overload: connectionString already includes the target database.
    /// </summary>
    public async Task<List<string>> GetPrimaryKeyColumnsFromConnAsync(string connectionString, string schema, string table)
        => await GetPrimaryKeyColumnsCoreAsync(connectionString, schema, table);

    private async Task<List<string>> GetPrimaryKeyColumnsCoreAsync(string connectionString, string schema, string table)
    {
        var pkColumns = new List<string>();

        // Extract database name from connection string for sys catalog queries
        var builder = new SqlConnectionStringBuilder(connectionString);
        var safeDb = builder.InitialCatalog.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT c.name
            FROM [{safeDb}].sys.index_columns ic
            JOIN [{safeDb}].sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            JOIN [{safeDb}].sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN [{safeDb}].sys.tables t ON i.object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE i.is_primary_key = 1
              AND s.name = @schema AND t.name = @table
            ORDER BY ic.key_ordinal";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            pkColumns.Add(reader.GetString(0));

        return pkColumns;
    }

    /// <summary>
    /// Apply all pending changes in a single transaction with row-count verification.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyChangesAsync(
        string database, string schema, string table,
        string[] columnNames, List<string> pkColumns,
        List<EditableRow> rows)
        => await ApplyChangesCoreAsync(_db.GetConnectionStringForDatabase(database), schema, table, columnNames, pkColumns, rows);

    /// <summary>
    /// Per-tab overload: connectionString already includes the target database.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyChangesFromConnAsync(
        string connectionString, string schema, string table,
        string[] columnNames, List<string> pkColumns,
        List<EditableRow> rows)
        => await ApplyChangesCoreAsync(connectionString, schema, table, columnNames, pkColumns, rows);

    private async Task<(bool Success, string Message)> ApplyChangesCoreAsync(
        string connectionString, string schema, string table,
        string[] columnNames, List<string> pkColumns,
        List<EditableRow> rows)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        int updates = 0, inserts = 0, deletes = 0;

        try
        {
            var pkIndices = pkColumns.Select(pk =>
                Array.FindIndex(columnNames, c => c.Equals(pk, StringComparison.OrdinalIgnoreCase))
            ).ToArray();

            if (pkIndices.Any(i => i < 0))
                return (false, "Primary key column not found in result set. Ensure PK columns are in your SELECT.");

            foreach (var row in rows)
            {
                switch (row.State)
                {
                    case RowEditState.Modified:
                    {
                        var changedIndices = row.GetChangedColumnIndices();
                        if (changedIndices.Count == 0) continue;

                        var setClauses = changedIndices
                            .Select(i => $"[{columnNames[i]}] = @set_{i}");
                        var whereClauses = pkIndices
                            .Select(i => $"[{columnNames[i]}] = @pk_{i}");

                        var sql = $"UPDATE [{schema}].[{table}] SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)}";

                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in changedIndices)
                            cmd.Parameters.AddWithValue($"@set_{i}", row.Values[i] ?? DBNull.Value);
                        foreach (var i in pkIndices)
                            cmd.Parameters.AddWithValue($"@pk_{i}", row.OriginalValues![i] ?? DBNull.Value);

                        var affected = await cmd.ExecuteNonQueryAsync();
                        if (affected != 1)
                        {
                            tx.Rollback();
                            return (false, $"UPDATE affected {affected} rows (expected 1). All changes rolled back.");
                        }
                        updates++;
                        break;
                    }

                    case RowEditState.New:
                    {
                        var colIndices = Enumerable.Range(0, columnNames.Length)
                            .Where(i => row.Values[i] != null)
                            .ToList();

                        if (colIndices.Count == 0) continue;

                        var cols = colIndices.Select(i => $"[{columnNames[i]}]");
                        var parms = colIndices.Select(i => $"@ins_{i}");

                        var sql = $"INSERT INTO [{schema}].[{table}] ({string.Join(", ", cols)}) VALUES ({string.Join(", ", parms)})";

                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in colIndices)
                            cmd.Parameters.AddWithValue($"@ins_{i}", row.Values[i] ?? DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                        inserts++;
                        break;
                    }

                    case RowEditState.Deleted:
                    {
                        var whereClauses = pkIndices
                            .Select(i => $"[{columnNames[i]}] = @pk_{i}");

                        var sql = $"DELETE FROM [{schema}].[{table}] WHERE {string.Join(" AND ", whereClauses)}";

                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in pkIndices)
                        {
                            var val = row.OriginalValues?[i] ?? row.Values[i];
                            cmd.Parameters.AddWithValue($"@pk_{i}", val ?? DBNull.Value);
                        }

                        var affected = await cmd.ExecuteNonQueryAsync();
                        if (affected != 1)
                        {
                            tx.Rollback();
                            return (false, $"DELETE affected {affected} rows (expected 1). All changes rolled back.");
                        }
                        deletes++;
                        break;
                    }
                }
            }

            tx.Commit();

            var parts = new List<string>();
            if (updates > 0) parts.Add($"{updates} update(s)");
            if (inserts > 0) parts.Add($"{inserts} insert(s)");
            if (deletes > 0) parts.Add($"{deletes} delete(s)");

            return (true, $"Applied: {string.Join(", ", parts)}");
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            return (false, $"Error: {ex.Message}. All changes rolled back.");
        }
    }

    /// <summary>
    /// Generate preview SQL for all pending changes (for "Show SQL" display).
    /// </summary>
    public static string GeneratePreviewSql(
        string schema, string table,
        string[] columnNames, List<string> pkColumns,
        List<EditableRow> rows)
    {
        var pkIndices = pkColumns.Select(pk =>
            Array.FindIndex(columnNames, c => c.Equals(pk, StringComparison.OrdinalIgnoreCase))
        ).ToArray();

        var lines = new List<string>();

        foreach (var row in rows)
        {
            switch (row.State)
            {
                case RowEditState.Modified:
                {
                    var changedIndices = row.GetChangedColumnIndices();
                    if (changedIndices.Count == 0) continue;

                    var setClauses = changedIndices
                        .Select(i => $"[{columnNames[i]}] = {FormatValue(row.Values[i])}");
                    var whereClauses = pkIndices
                        .Select(i => $"[{columnNames[i]}] = {FormatValue(row.OriginalValues![i])}");

                    lines.Add($"UPDATE [{schema}].[{table}]");
                    lines.Add($"SET {string.Join(", ", setClauses)}");
                    lines.Add($"WHERE {string.Join(" AND ", whereClauses)};");
                    lines.Add("");
                    break;
                }

                case RowEditState.New:
                {
                    var colIndices = Enumerable.Range(0, columnNames.Length)
                        .Where(i => row.Values[i] != null)
                        .ToList();

                    if (colIndices.Count == 0) continue;

                    var cols = colIndices.Select(i => $"[{columnNames[i]}]");
                    var vals = colIndices.Select(i => FormatValue(row.Values[i]));

                    lines.Add($"INSERT INTO [{schema}].[{table}] ({string.Join(", ", cols)})");
                    lines.Add($"VALUES ({string.Join(", ", vals)});");
                    lines.Add("");
                    break;
                }

                case RowEditState.Deleted:
                {
                    var whereClauses = pkIndices
                        .Select(i =>
                        {
                            var val = row.OriginalValues?[i] ?? row.Values[i];
                            return $"[{columnNames[i]}] = {FormatValue(val)}";
                        });

                    lines.Add($"DELETE FROM [{schema}].[{table}]");
                    lines.Add($"WHERE {string.Join(" AND ", whereClauses)};");
                    lines.Add("");
                    break;
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        string s => $"'{s.Replace("'", "''")}'",
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'",
        bool b => b ? "1" : "0",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        decimal or float or double or int or long or short or byte or sbyte => value.ToString() ?? "NULL",
        _ => $"/* unsupported: {value.GetType().Name} */"
    };
}
