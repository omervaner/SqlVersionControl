using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
    // ── Object Explorer schema queries ──────────────────────────────

    // ── Object Explorer schema queries (with per-tab overloads) ────

    public async Task<List<(string Schema, string Name)>> GetTablesAsync(string database)
        => await GetTablesAsync(_connectionString, database);

    public async Task<List<(string Schema, string Name)>> GetTablesAsync(string connectionString, string database)
    {
        var results = new List<(string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name, t.name
            FROM [{safeDb}].sys.tables t
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1)));

        return results;
    }

    public async Task<Dictionary<string, long>> GetTableRowCountsAsync(string database)
        => await GetTableRowCountsAsync(_connectionString, database);

    public async Task<Dictionary<string, long>> GetTableRowCountsAsync(string connectionString, string database)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var connStr = BuildConnectionString(connectionString, database);
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var sql = @"
            SELECT s.name + '.' + t.name, SUM(p.row_count)
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.dm_db_partition_stats p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
            GROUP BY s.name, t.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetInt64(1);

        return result;
    }

    public async Task<Dictionary<string, int>> GetObjectCountsAsync(string connectionString, string database)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var connStr = BuildConnectionString(connectionString, database);
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var sql = @"
            SELECT
                CASE o.type
                    WHEN 'U'  THEN 'Tables'
                    WHEN 'V'  THEN 'Views'
                    WHEN 'P'  THEN 'Stored Procedures'
                    WHEN 'FN' THEN 'Functions'
                    WHEN 'IF' THEN 'Functions'
                    WHEN 'TF' THEN 'Functions'
                    WHEN 'TR' THEN 'Triggers'
                END AS Category,
                COUNT(*) AS Cnt
            FROM sys.objects o
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
            GROUP BY CASE o.type
                    WHEN 'U'  THEN 'Tables'
                    WHEN 'V'  THEN 'Views'
                    WHEN 'P'  THEN 'Stored Procedures'
                    WHEN 'FN' THEN 'Functions'
                    WHEN 'IF' THEN 'Functions'
                    WHEN 'TF' THEN 'Functions'
                    WHEN 'TR' THEN 'Triggers'
                END";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cat = reader.GetString(0);
            var cnt = reader.GetInt32(1);
            if (result.ContainsKey(cat)) result[cat] += cnt;
            else result[cat] = cnt;
        }

        // Sequences
        try
        {
            using var seqCmd = new SqlCommand("SELECT COUNT(*) FROM sys.sequences WHERE is_ms_shipped = 0", conn);
            result["Sequences"] = (int)(await seqCmd.ExecuteScalarAsync() ?? 0);
        }
        catch { }

        // Jobs
        try
        {
            using var jobCmd = new SqlCommand("SELECT COUNT(*) FROM msdb.dbo.sysjobs", conn);
            result["Jobs"] = (int)(await jobCmd.ExecuteScalarAsync() ?? 0);
        }
        catch { }

        // User-defined types
        try
        {
            using var typeCmd = new SqlCommand("SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1", conn);
            result["Types"] = (int)(await typeCmd.ExecuteScalarAsync() ?? 0);
        }
        catch { }

        // Database triggers
        try
        {
            using var dtCmd = new SqlCommand("SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0", conn);
            result["Database Triggers"] = (int)(await dtCmd.ExecuteScalarAsync() ?? 0);
        }
        catch { }

        return result;
    }

    public record TableProperties(
        long RowCount, double DataSizeMB, double IndexSizeMB,
        DateTime CreateDate, DateTime? ModifyDate,
        int ColumnCount, int IndexCount);

    public async Task<TableProperties?> GetTablePropertiesAsync(
        string connectionString, string database, string schema, string tableName)
    {
        var connStr = BuildConnectionString(connectionString, database);
        using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var sql = @"
            SELECT
                SUM(p.row_count),
                SUM(CASE WHEN a.type = 1 THEN a.total_pages END) * 8.0 / 1024,
                SUM(CASE WHEN a.type = 2 THEN a.total_pages END) * 8.0 / 1024,
                t.create_date, t.modify_date,
                (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = t.object_id),
                (SELECT COUNT(*) FROM sys.indexes i WHERE i.object_id = t.object_id AND i.index_id > 0)
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.dm_db_partition_stats p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
            LEFT JOIN sys.allocation_units a ON p.partition_id = a.container_id
            WHERE s.name = @schema AND t.name = @table
            GROUP BY t.object_id, t.create_date, t.modify_date";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new TableProperties(
            RowCount: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            DataSizeMB: reader.IsDBNull(1) ? 0 : Math.Round(Convert.ToDouble(reader.GetValue(1)), 2),
            IndexSizeMB: reader.IsDBNull(2) ? 0 : Math.Round(Convert.ToDouble(reader.GetValue(2)), 2),
            CreateDate: reader.GetDateTime(3),
            ModifyDate: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            ColumnCount: reader.GetInt32(5),
            IndexCount: reader.GetInt32(6));
    }

    public async Task<List<(string Schema, string Name)>> GetViewsAsync(string database)
        => await GetViewsAsync(_connectionString, database);

    public async Task<List<(string Schema, string Name)>> GetViewsAsync(string connectionString, string database)
    {
        var results = new List<(string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name, v.name
            FROM [{safeDb}].sys.views v
            JOIN [{safeDb}].sys.schemas s ON v.schema_id = s.schema_id
            WHERE v.is_ms_shipped = 0
            ORDER BY s.name, v.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1)));

        return results;
    }

    public async Task<List<(string Schema, string Name, string Type)>> GetProcsAndFunctionsAsync(string database)
        => await GetProcsAndFunctionsAsync(_connectionString, database);

    public async Task<List<(string Schema, string Name, string Type)>> GetProcsAndFunctionsAsync(string connectionString, string database)
    {
        var results = new List<(string, string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name, o.name, o.type_desc
            FROM [{safeDb}].sys.objects o
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('P', 'FN', 'TF', 'IF')
              AND o.is_ms_shipped = 0
            ORDER BY o.type_desc, s.name, o.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return results;
    }

    public async Task<List<(string Schema, string Name, string DataType, long CurrentValue)>> GetSequencesAsync(string database)
        => await GetSequencesAsync(_connectionString, database);

    public async Task<List<(string Schema, string Name, string DataType, long CurrentValue)>> GetSequencesAsync(
        string connectionString, string database)
    {
        var results = new List<(string, string, string, long)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name, seq.name, TYPE_NAME(seq.user_type_id), CAST(seq.current_value AS BIGINT)
            FROM [{safeDb}].sys.sequences seq
            JOIN [{safeDb}].sys.schemas s ON seq.schema_id = s.schema_id
            ORDER BY s.name, seq.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                          reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));

        return results;
    }

    public async Task<List<(string Schema, string Name, string ParentTable, bool IsEnabled)>> GetTriggersAsync(string database)
        => await GetTriggersAsync(_connectionString, database);

    public async Task<List<(string Schema, string Name, string ParentTable, bool IsEnabled)>> GetTriggersAsync(
        string connectionString, string database)
    {
        var results = new List<(string, string, string, bool)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT s.name, t.name, OBJECT_NAME(t.parent_id, DB_ID('{database.Replace("'", "''")}')), t.is_disabled
            FROM [{safeDb}].sys.triggers t
            JOIN [{safeDb}].sys.objects o ON t.parent_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE t.parent_class = 1
            ORDER BY s.name, t.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1),
                          reader.IsDBNull(2) ? "" : reader.GetString(2),
                          !reader.GetBoolean(3))); // is_disabled → IsEnabled (inverted)

        return results;
    }

    public async Task<List<(string Name, bool IsEnabled, string EventTypes)>>
        GetDatabaseTriggersAsync(string database)
        => await GetDatabaseTriggersAsync(_connectionString, database);

    public async Task<List<(string Name, bool IsEnabled, string EventTypes)>>
        GetDatabaseTriggersAsync(string connectionString, string database)
    {
        var results = new List<(string, bool, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT t.name, ~t.is_disabled,
                   STUFF((
                       SELECT ', ' + te.type_desc
                       FROM [{safeDb}].sys.trigger_events te
                       WHERE te.object_id = t.object_id
                       FOR XML PATH('')
                   ), 1, 2, '') AS EventTypes
            FROM [{safeDb}].sys.triggers t
            WHERE t.parent_class_desc = 'DATABASE'
            ORDER BY t.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)
            ));
        }

        return results;
    }

    public async Task<List<(string Name, string BaseType, bool IsTableType)>>
        GetUserTypesAsync(string database)
        => await GetUserTypesAsync(_connectionString, database);

    public async Task<List<(string Name, string BaseType, bool IsTableType)>>
        GetUserTypesAsync(string connectionString, string database)
    {
        var results = new List<(string, string, bool)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT t.name,
                   CASE WHEN t.is_table_type = 1 THEN 'Table Type'
                        ELSE TYPE_NAME(t.system_type_id)
                   END AS BaseType,
                   t.is_table_type
            FROM [{safeDb}].sys.types t
            WHERE t.is_user_defined = 1
            ORDER BY t.is_table_type, t.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2)
            ));
        }

        return results;
    }

    public async Task AlterSequenceRestartAsync(string connectionString, string database, string schema, string name, long restartValue)
    {
        var safeDb = database.Replace("]", "]]");
        var safeSchema = schema.Replace("]", "]]");
        var safeName = name.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $"ALTER SEQUENCE [{safeDb}].[{safeSchema}].[{safeName}] RESTART WITH {restartValue}";
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetObjectDefinitionAsync(string database, string schema, string objectName)
        => await GetObjectDefinitionAsync(_connectionString, database, schema, objectName);

    public async Task<string?> GetObjectDefinitionAsync(string connectionString, string database, string schema, string objectName)
    {
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT m.definition
            FROM [{safeDb}].sys.sql_modules m
            JOIN [{safeDb}].sys.objects o ON m.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @objectName";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@objectName", objectName);

        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task<List<(string Name, string TypeName, int MaxLength, byte Precision, byte Scale, bool IsOutput)>>
        GetProcParametersDetailedAsync(string database, string schema, string procName)
        => await GetProcParametersDetailedAsync(_connectionString, database, schema, procName);

    public async Task<List<(string Name, string TypeName, int MaxLength, byte Precision, byte Scale, bool IsOutput)>>
        GetProcParametersDetailedAsync(string connectionString, string database, string schema, string procName)
    {
        var results = new List<(string, string, int, byte, byte, bool)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT p.name, TYPE_NAME(p.user_type_id) AS TypeName,
                   p.max_length, p.precision, p.scale, p.is_output
            FROM [{safeDb}].sys.parameters p
            JOIN [{safeDb}].sys.objects o ON p.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @procName
            ORDER BY p.parameter_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@procName", procName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1),
                reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4), reader.GetBoolean(5)));

        return results;
    }

    public async Task<List<(string Name, string TypeName)>> GetProcParametersAsync(
        string database, string schema, string procName)
        => await GetProcParametersAsync(_connectionString, database, schema, procName);

    public async Task<List<(string Name, string TypeName)>> GetProcParametersAsync(
        string connectionString, string database, string schema, string procName)
    {
        var results = new List<(string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT p.name, TYPE_NAME(p.user_type_id) AS TypeName
            FROM [{safeDb}].sys.parameters p
            JOIN [{safeDb}].sys.objects o ON p.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @procName
            ORDER BY p.parameter_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@procName", procName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetString(1)));

        return results;
    }

    public async Task<List<(string Name, string TypeName, int MaxLength, bool IsNullable, bool IsPrimaryKey)>>
        GetColumnsAsync(string database, string schema, string table)
        => await GetColumnsAsync(_connectionString, database, schema, table);

    public async Task<List<(string Name, string TypeName, int MaxLength, bool IsNullable, bool IsPrimaryKey)>>
        GetColumnsAsync(string connectionString, string database, string schema, string table)
    {
        var results = new List<(string, string, int, bool, bool)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT c.name,
                   TYPE_NAME(c.user_type_id) AS TypeName,
                   c.max_length,
                   c.is_nullable,
                   CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
            FROM [{safeDb}].sys.columns c
            JOIN [{safeDb}].sys.tables t ON c.object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN [{safeDb}].sys.index_columns ic
              ON ic.object_id = c.object_id AND ic.column_id = c.column_id
              AND ic.index_id = (SELECT TOP 1 i.index_id FROM [{safeDb}].sys.indexes i
                                 WHERE i.object_id = t.object_id AND i.is_primary_key = 1)
            WHERE s.name = @schema AND t.name = @table
            ORDER BY c.column_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetBoolean(3),
                reader.GetInt32(4) == 1
            ));
        }

        return results;
    }

    // ── Object Explorer: Indexes, Foreign Keys, Constraints ───────────

    public async Task<List<(string Name, string TypeDescription, string KeyColumns, string IncludedColumns)>>
        GetTableIndexesAsync(string database, string schema, string table)
        => await GetTableIndexesAsync(_connectionString, database, schema, table);

    public async Task<List<(string Name, string TypeDescription, string KeyColumns, string IncludedColumns)>>
        GetTableIndexesAsync(string connectionString, string database, string schema, string table)
    {
        var results = new List<(string, string, string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT i.name,
                   CASE
                       WHEN i.is_primary_key = 1 THEN 'Primary Key'
                       WHEN i.is_unique_constraint = 1 THEN 'Unique Constraint'
                       WHEN i.is_unique = 1 THEN 'Unique ' + LOWER(i.type_desc)
                       ELSE CASE i.type
                           WHEN 1 THEN 'Clustered'
                           WHEN 2 THEN 'Nonclustered'
                           WHEN 5 THEN 'Clustered columnstore'
                           WHEN 6 THEN 'Nonclustered columnstore'
                           ELSE LOWER(i.type_desc)
                       END
                   END AS TypeDescription,
                   STUFF((
                       SELECT ', ' + c.name
                       FROM [{safeDb}].sys.index_columns ic
                       JOIN [{safeDb}].sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                       ORDER BY ic.key_ordinal
                       FOR XML PATH('')
                   ), 1, 2, '') AS KeyColumns,
                   STUFF((
                       SELECT ', ' + c.name
                       FROM [{safeDb}].sys.index_columns ic
                       JOIN [{safeDb}].sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                       ORDER BY ic.key_ordinal
                       FOR XML PATH('')
                   ), 1, 2, '') AS IncludedColumns
            FROM [{safeDb}].sys.indexes i
            JOIN [{safeDb}].sys.tables t ON i.object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schema AND t.name = @table
              AND i.type > 0  -- exclude heap
              AND i.name IS NOT NULL
            ORDER BY i.is_primary_key DESC, i.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)
            ));
        }

        return results;
    }

    public async Task<List<(string Name, string ReferencedTable, string ColumnMapping, string DeleteAction, string UpdateAction)>>
        GetForeignKeysAsync(string database, string schema, string table)
        => await GetForeignKeysAsync(_connectionString, database, schema, table);

    public async Task<List<(string Name, string ReferencedTable, string ColumnMapping, string DeleteAction, string UpdateAction)>>
        GetForeignKeysAsync(string connectionString, string database, string schema, string table)
    {
        var results = new List<(string, string, string, string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT fk.name,
                   QUOTENAME(rs.name) + '.' + QUOTENAME(rt.name) AS ReferencedTable,
                   STUFF((
                       SELECT ', ' + pc.name + ' → ' + rc.name
                       FROM [{safeDb}].sys.foreign_key_columns fkc
                       JOIN [{safeDb}].sys.columns pc ON fkc.parent_object_id = pc.object_id AND fkc.parent_column_id = pc.column_id
                       JOIN [{safeDb}].sys.columns rc ON fkc.referenced_object_id = rc.object_id AND fkc.referenced_column_id = rc.column_id
                       WHERE fkc.constraint_object_id = fk.object_id
                       ORDER BY fkc.constraint_column_id
                       FOR XML PATH('')
                   ), 1, 2, '') AS ColumnMapping,
                   fk.delete_referential_action_desc,
                   fk.update_referential_action_desc
            FROM [{safeDb}].sys.foreign_keys fk
            JOIN [{safeDb}].sys.tables pt ON fk.parent_object_id = pt.object_id
            JOIN [{safeDb}].sys.schemas ps ON pt.schema_id = ps.schema_id
            JOIN [{safeDb}].sys.tables rt ON fk.referenced_object_id = rt.object_id
            JOIN [{safeDb}].sys.schemas rs ON rt.schema_id = rs.schema_id
            WHERE ps.name = @schema AND pt.name = @table
            ORDER BY fk.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            ));
        }

        return results;
    }

    public async Task<List<(string Name, string ConstraintType, string Expression, string ColumnName)>>
        GetConstraintsAsync(string database, string schema, string table)
        => await GetConstraintsAsync(_connectionString, database, schema, table);

    public async Task<List<(string Name, string ConstraintType, string Expression, string ColumnName)>>
        GetConstraintsAsync(string connectionString, string database, string schema, string table)
    {
        var results = new List<(string, string, string, string)>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            -- Check constraints
            SELECT cc.name, 'CHECK' AS ConstraintType, cc.definition, ''
            FROM [{safeDb}].sys.check_constraints cc
            JOIN [{safeDb}].sys.tables t ON cc.parent_object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schema AND t.name = @table
            UNION ALL
            -- Default constraints
            SELECT dc.name, 'DEFAULT', dc.definition, c.name
            FROM [{safeDb}].sys.default_constraints dc
            JOIN [{safeDb}].sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            JOIN [{safeDb}].sys.tables t ON dc.parent_object_id = t.object_id
            JOIN [{safeDb}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY ConstraintType, 1";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)
            ));
        }

        return results;
    }

    // ── Bulk column loader (for intellisense) ────────────────────────

    public async Task<Dictionary<string, List<string>>> GetAllColumnsAsync(string database)
        => await GetAllColumnsAsync(_connectionString, database);

    public async Task<Dictionary<string, List<string>>> GetAllColumnsAsync(string connectionString, string database)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
            FROM [{safeDb}].INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!result.TryGetValue(key, out var cols))
            {
                cols = new List<string>();
                result[key] = cols;
            }
            cols.Add(reader.GetString(2));
        }
        return result;
    }

    // ── Server-side Object Search (for OE filter) ───────────────────

    /// <summary>
    /// Search for objects matching a filter across all specified databases on a connection.
    /// Returns (Database, Schema, Name, TypeCode) tuples.
    /// TypeCode: U=Table, V=View, P=Proc, FN/TF/IF=Function, SO=Sequence, TR=Trigger
    /// </summary>
    public async Task<List<(string Database, string Schema, string Name, string TypeCode)>> SearchObjectsAsync(
        string connectionString, IEnumerable<string> databases, string filter, CancellationToken ct)
    {
        var results = new List<(string, string, string, string)>();
        if (string.IsNullOrWhiteSpace(filter)) return results;

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Build a UNION ALL across all databases for a single round-trip
        var unions = new List<string>();
        foreach (var db in databases)
        {
            ct.ThrowIfCancellationRequested();
            var safeDb = db.Replace("]", "]]");
            unions.Add($@"
                SELECT '{safeDb}' AS db, s.name AS [schema], o.name, o.type
                FROM [{safeDb}].sys.objects o
                JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.is_ms_shipped = 0
                  AND o.type IN ('U','V','P','FN','TF','IF','SO','TR')
                  AND o.name LIKE @filter");
        }

        if (unions.Count == 0) return results;

        var sql = string.Join("\nUNION ALL\n", unions) + "\nORDER BY db, [schema], name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@filter", $"%{filter}%");

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3).Trim()
            ));
        }

        return results;
    }
}
