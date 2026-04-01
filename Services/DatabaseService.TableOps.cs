using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
    // ── Git Export helpers ──────────────────────────────────────────

    /// <summary>
    /// Gets all databases, optionally including system databases (master, msdb, model, tempdb).
    /// </summary>
    public async Task<List<string>> GetAllDatabasesAsync(string connectionString, bool includeSystem)
    {
        var databases = new List<string>();
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = includeSystem
            ? "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name"
            : "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' ORDER BY name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            databases.Add(reader.GetString(0));

        return databases;
    }

    /// <summary>
    /// Gets all code object definitions (procs, functions, views, triggers) for a database in one query.
    /// </summary>
    public async Task<List<(string Schema, string Name, string TypeDesc, string Definition)>>
        GetAllCodeObjectsAsync(string connectionString, string database)
    {
        var results = new List<(string, string, string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name AS SchemaName, o.name AS ObjectName, o.type_desc, m.definition
            FROM [{safeDb}].sys.sql_modules m
            JOIN [{safeDb}].sys.objects o ON m.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.is_ms_shipped = 0";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var definition = reader.IsDBNull(3) ? "" : reader.GetString(3);
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), definition));
        }

        return results;
    }

    /// <summary>
    /// Gets all tables with their columns for a database (for bulk CREATE TABLE script generation).
    /// Returns columns grouped by (Schema, TableName).
    /// </summary>
    public async Task<Dictionary<(string Schema, string Table), List<(string Name, string TypeName, int MaxLength, bool IsNullable, bool IsPrimaryKey)>>>
        GetAllTableColumnsAsync(string connectionString, string database)
    {
        var results = new Dictionary<(string, string), List<(string, string, int, bool, bool)>>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name AS SchemaName, t.name AS TableName,
                   c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS TypeName,
                   c.max_length, c.is_nullable,
                   CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
            FROM [{safeDb}].sys.columns c
            JOIN [{safeDb}].sys.tables t ON c.object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN [{safeDb}].sys.index_columns ic
              ON ic.object_id = c.object_id AND ic.column_id = c.column_id
              AND ic.index_id = (SELECT TOP 1 i.index_id FROM [{safeDb}].sys.indexes i
                                 WHERE i.object_id = t.object_id AND i.is_primary_key = 1)
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!results.ContainsKey(key))
                results[key] = new List<(string, string, int, bool, bool)>();

            results[key].Add((
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt16(4),
                reader.GetBoolean(5),
                reader.GetInt32(6) == 1
            ));
        }

        return results;
    }

    // ── Shared CREATE TABLE script generation ───────────────────────

    /// <summary>
    /// Generates a CREATE TABLE script from column metadata.
    /// Single source of truth — used by ObjectExplorerViewModel and GitExportService.
    /// </summary>
    public static string GenerateCreateTableScript(
        string schema, string tableName,
        List<(string Name, string TypeName, int MaxLength, bool IsNullable, bool IsPrimaryKey)> columns)
    {
        var pkCols = columns.Where(c => c.IsPrimaryKey).Select(c => $"[{c.Name}]").ToList();
        var colDefs = columns.Select(c =>
        {
            var typeFmt = SqlTypeFormatter.Format(c.TypeName, c.MaxLength);
            var nullable = c.IsNullable ? "NULL" : "NOT NULL";
            return $"    [{c.Name}] {typeFmt} {nullable}";
        });

        var sql = $"CREATE TABLE [{schema}].[{tableName}]\n(\n{string.Join(",\n", colDefs)}";
        if (pkCols.Count > 0)
            sql += $",\n    CONSTRAINT [PK_{tableName}] PRIMARY KEY ({string.Join(", ", pkCols)})";
        sql += "\n)";

        return sql;
    }
}
