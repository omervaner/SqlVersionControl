using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class DataCompareService
{
    /// <summary>
    /// Detect primary key columns for a table.
    /// </summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(string connectionString, string schema, string table)
    {
        var pkColumns = new List<string>();
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
    /// Detect if a table has an identity column, and return its name.
    /// </summary>
    public async Task<string?> GetIdentityColumnAsync(string connectionString, string schema, string table)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var safeDb = builder.InitialCatalog.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT c.name
            FROM [{safeDb}].sys.columns c
            JOIN [{safeDb}].sys.tables t ON c.object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE c.is_identity = 1
              AND s.name = @schema AND t.name = @table";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>
    /// Fetch all rows from a table (up to maxRows). Returns column names and row data.
    /// </summary>
    public async Task<(string[] ColumnNames, List<object?[]> Rows)> FetchRowsAsync(
        string connectionString, string schema, string table, int maxRows = 1000)
    {
        var safeSchema = schema.Replace("]", "]]");
        var safeName = table.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $"SELECT TOP ({maxRows}) * FROM [{safeSchema}].[{safeName}] WITH (NOLOCK)";
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();

        var columnCount = reader.FieldCount;
        var columnNames = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
            columnNames[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object?[columnCount];
            for (int i = 0; i < columnCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }

        return (columnNames, rows);
    }

    /// <summary>
    /// Compare row data between source and target tables, matched by PK.
    /// </summary>
    public async Task<DataCompareResult> CompareDataAsync(
        string sourceConnStr, string targetConnStr,
        string schema, string table,
        int maxRows = 1000)
    {
        // 1. Detect PK from source
        var pkColumns = await GetPrimaryKeyColumnsAsync(sourceConnStr, schema, table);
        if (pkColumns.Count == 0)
        {
            // Try target as fallback
            pkColumns = await GetPrimaryKeyColumnsAsync(targetConnStr, schema, table);
        }

        if (pkColumns.Count == 0)
            throw new InvalidOperationException(
                $"No primary key found on {schema}.{table}. Select matching columns manually.");

        // 2. Detect identity column + fetch rows in parallel
        var identityTask = GetIdentityColumnAsync(targetConnStr, schema, table);
        var sourceTask = FetchRowsAsync(sourceConnStr, schema, table, maxRows);
        var targetTask = FetchRowsAsync(targetConnStr, schema, table, maxRows);
        await Task.WhenAll(identityTask, sourceTask, targetTask);

        var identityColumn = identityTask.Result;

        var (sourceColumns, sourceRows) = sourceTask.Result;
        var (targetColumns, targetRows) = targetTask.Result;

        // Use source column names as canonical (they should match)
        var columnNames = sourceColumns.Length > 0 ? sourceColumns : targetColumns;

        // 3. Find PK column indices
        var pkIndices = pkColumns
            .Select(pk => Array.FindIndex(columnNames, c =>
                c.Equals(pk, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0)
            .ToArray();

        if (pkIndices.Length == 0)
            throw new InvalidOperationException("PK columns not found in query results.");

        // 4. Index target rows by PK for fast lookup
        var targetByPk = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var row in targetRows)
            targetByPk[BuildPkKey(row, pkIndices)] = row;

        // 5. Match and compare
        var result = new DataCompareResult
        {
            Schema = schema,
            TableName = table,
            ColumnNames = columnNames,
            PkColumnNames = pkColumns.ToArray(),
            PkColumnIndices = pkIndices,
            HasIdentityColumn = identityColumn != null,
            Rows = []
        };

        var matchedPks = new HashSet<string>(StringComparer.Ordinal);

        // Process source rows
        foreach (var srcRow in sourceRows)
        {
            var pkKey = BuildPkKey(srcRow, pkIndices);
            matchedPks.Add(pkKey);

            if (targetByPk.TryGetValue(pkKey, out var tgtRow))
            {
                var diffCols = FindDifferences(srcRow, tgtRow, columnNames.Length);
                result.Rows.Add(new DataCompareRow
                {
                    Status = diffCols.Count == 0 ? DataRowStatus.Identical : DataRowStatus.Different,
                    SourceValues = srcRow,
                    TargetValues = tgtRow,
                    DifferentColumnIndices = diffCols
                });
            }
            else
            {
                result.Rows.Add(new DataCompareRow
                {
                    Status = DataRowStatus.SourceOnly,
                    SourceValues = srcRow,
                    TargetValues = new object?[columnNames.Length]
                });
            }
        }

        // Process target-only rows
        foreach (var tgtRow in targetRows)
        {
            var pkKey = BuildPkKey(tgtRow, pkIndices);
            if (!matchedPks.Contains(pkKey))
            {
                result.Rows.Add(new DataCompareRow
                {
                    Status = DataRowStatus.TargetOnly,
                    SourceValues = new object?[columnNames.Length],
                    TargetValues = tgtRow
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Generate SQL scripts to deploy selected rows from source to target.
    /// </summary>
    public static string GenerateDeploySql(
        DataCompareResult comparison,
        IEnumerable<DataCompareRow> rows)
    {
        var schema = comparison.Schema.Replace("]", "]]");
        var table = comparison.TableName.Replace("]", "]]");
        var tableRef = $"[{schema}].[{table}]";
        var cols = comparison.ColumnNames;
        var pkIndices = comparison.PkColumnIndices;
        var lines = new List<string>();
        var rowList = rows.ToList();

        var needsIdentityInsert = comparison.HasIdentityColumn &&
            rowList.Any(r => r.Status == DataRowStatus.SourceOnly);

        if (needsIdentityInsert)
        {
            lines.Add($"SET IDENTITY_INSERT {tableRef} ON;");
            lines.Add("");
        }

        foreach (var row in rowList)
        {
            switch (row.Status)
            {
                case DataRowStatus.Different:
                {
                    // UPDATE target to match source for differing columns
                    var setClauses = row.DifferentColumnIndices
                        .Where(i => !pkIndices.Contains(i))
                        .Select(i => $"[{cols[i]}] = {FormatValue(row.SourceValues[i])}");
                    var whereClauses = pkIndices
                        .Select(i => $"[{cols[i]}] = {FormatValue(row.SourceValues[i])}");

                    lines.Add($"UPDATE {tableRef}");
                    lines.Add($"SET {string.Join(", ", setClauses)}");
                    lines.Add($"WHERE {string.Join(" AND ", whereClauses)};");
                    lines.Add("");
                    break;
                }

                case DataRowStatus.SourceOnly:
                {
                    // INSERT into target
                    var nonNullIndices = Enumerable.Range(0, cols.Length)
                        .Where(i => row.SourceValues[i] != null)
                        .ToList();
                    var colList = nonNullIndices.Select(i => $"[{cols[i]}]");
                    var valList = nonNullIndices.Select(i => FormatValue(row.SourceValues[i]));

                    lines.Add($"INSERT INTO {tableRef} ({string.Join(", ", colList)})");
                    lines.Add($"VALUES ({string.Join(", ", valList)});");
                    lines.Add("");
                    break;
                }

                case DataRowStatus.TargetOnly:
                {
                    // DELETE from target
                    var whereClauses = pkIndices
                        .Select(i => $"[{cols[i]}] = {FormatValue(row.TargetValues[i])}");

                    lines.Add($"DELETE FROM {tableRef}");
                    lines.Add($"WHERE {string.Join(" AND ", whereClauses)};");
                    lines.Add("");
                    break;
                }
            }
        }

        if (needsIdentityInsert)
        {
            lines.Add($"SET IDENTITY_INSERT {tableRef} OFF;");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Execute deploy scripts against the target database in a transaction.
    /// </summary>
    public async Task<(bool Success, string Message)> DeployRowsAsync(
        string targetConnStr, DataCompareResult comparison, IEnumerable<DataCompareRow> rows)
    {
        var schema = comparison.Schema.Replace("]", "]]");
        var table = comparison.TableName.Replace("]", "]]");
        var tableRef = $"[{schema}].[{table}]";
        var cols = comparison.ColumnNames;
        var pkIndices = comparison.PkColumnIndices;

        using var conn = new SqlConnection(targetConnStr);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        int updates = 0, inserts = 0, deletes = 0;
        var rowList = rows.ToList();
        var needsIdentityInsert = comparison.HasIdentityColumn &&
            rowList.Any(r => r.Status == DataRowStatus.SourceOnly);

        try
        {
            if (needsIdentityInsert)
            {
                using var idOn = new SqlCommand($"SET IDENTITY_INSERT {tableRef} ON", conn, tx);
                await idOn.ExecuteNonQueryAsync();
            }

            foreach (var row in rowList)
            {
                switch (row.Status)
                {
                    case DataRowStatus.Different:
                    {
                        var setIndices = row.DifferentColumnIndices
                            .Where(i => !pkIndices.Contains(i)).ToList();
                        var setClauses = setIndices.Select(i => $"[{cols[i]}] = @set_{i}");
                        var whereClauses = pkIndices.Select(i => $"[{cols[i]}] = @pk_{i}");

                        var sql = $"UPDATE {tableRef} SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)}";
                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in setIndices)
                            cmd.Parameters.AddWithValue($"@set_{i}", row.SourceValues[i] ?? DBNull.Value);
                        foreach (var i in pkIndices)
                            cmd.Parameters.AddWithValue($"@pk_{i}", row.SourceValues[i] ?? DBNull.Value);

                        var affected = await cmd.ExecuteNonQueryAsync();
                        if (affected != 1)
                            throw new InvalidOperationException($"UPDATE affected {affected} rows (expected 1)");
                        updates++;
                        break;
                    }

                    case DataRowStatus.SourceOnly:
                    {
                        var nonNullIndices = Enumerable.Range(0, cols.Length)
                            .Where(i => row.SourceValues[i] != null).ToList();
                        var colList = nonNullIndices.Select(i => $"[{cols[i]}]");
                        var parms = nonNullIndices.Select(i => $"@ins_{i}");

                        var sql = $"INSERT INTO {tableRef} ({string.Join(", ", colList)}) VALUES ({string.Join(", ", parms)})";
                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in nonNullIndices)
                            cmd.Parameters.AddWithValue($"@ins_{i}", row.SourceValues[i] ?? DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                        inserts++;
                        break;
                    }

                    case DataRowStatus.TargetOnly:
                    {
                        var whereClauses = pkIndices.Select(i => $"[{cols[i]}] = @pk_{i}");
                        var sql = $"DELETE FROM {tableRef} WHERE {string.Join(" AND ", whereClauses)}";
                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 30 };
                        foreach (var i in pkIndices)
                            cmd.Parameters.AddWithValue($"@pk_{i}", row.TargetValues[i] ?? DBNull.Value);

                        var affected = await cmd.ExecuteNonQueryAsync();
                        if (affected != 1)
                            throw new InvalidOperationException($"DELETE affected {affected} rows (expected 1)");
                        deletes++;
                        break;
                    }
                }
            }

            if (needsIdentityInsert)
            {
                using var idOff = new SqlCommand($"SET IDENTITY_INSERT {tableRef} OFF", conn, tx);
                await idOff.ExecuteNonQueryAsync();
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

    // --- Private helpers ---

    private static string BuildPkKey(object?[] row, int[] pkIndices)
    {
        return string.Join("|", pkIndices.Select(i =>
            row[i]?.ToString() ?? "\0NULL\0"));
    }

    private static HashSet<int> FindDifferences(object?[] source, object?[] target, int columnCount)
    {
        var diffs = new HashSet<int>();
        for (int i = 0; i < columnCount; i++)
        {
            if (!ValuesEqual(source.ElementAtOrDefault(i), target.ElementAtOrDefault(i)))
                diffs.Add(i);
        }
        return diffs;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Handle byte arrays (varbinary, image, etc.)
        if (a is byte[] ba && b is byte[] bb)
            return ba.SequenceEqual(bb);

        // Compare by string representation for cross-type compatibility
        return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "NULL";
        return value switch
        {
            string s => $"N'{s.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffffff zzz}'",
            bool b => b ? "1" : "0",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            decimal or float or double => value.ToString()!,
            _ => value is IFormattable
                ? value.ToString()!
                : $"N'{value.ToString()!.Replace("'", "''")}'"
        };
    }
}
