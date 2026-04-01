using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
    // ── Index Analysis DMV Queries ───────────────────────────────────

    public async Task<DateTime?> GetServerStartTimeAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT sqlserver_start_time FROM sys.dm_os_sys_info", conn);
            var result = await cmd.ExecuteScalarAsync();
            return result as DateTime?;
        }
        catch { return null; }
    }

    public async Task<List<Dictionary<string, object?>>> GetUnusedIndexesAsync(string connectionString, string database)
    {
        var safeDb = database.Replace("]", "]]");
        var safeDbStr = database.Replace("'", "''");
        var rows = new List<Dictionary<string, object?>>();

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            USE [{safeDb}];
            SELECT
                s.name + '.' + o.name AS [Schema.Table],
                i.name AS [Index Name],
                CASE
                    WHEN i.is_primary_key = 1 THEN 'Primary Key'
                    WHEN i.is_unique = 1 AND i.type = 1 THEN 'Unique Clustered'
                    WHEN i.is_unique = 1 THEN 'Unique Nonclustered'
                    WHEN i.type = 1 THEN 'Clustered'
                    ELSE 'Nonclustered'
                END AS [Type],
                i.is_primary_key AS [IsPK],
                i.type AS [IndexType],
                ISNULL(us.user_seeks, 0) AS [User Seeks],
                ISNULL(us.user_scans, 0) AS [User Scans],
                ISNULL(us.user_lookups, 0) AS [User Lookups],
                ISNULL(us.user_updates, 0) AS [User Updates],
                ISNULL(us.user_seeks, 0) + ISNULL(us.user_scans, 0) + ISNULL(us.user_lookups, 0) AS [Total Reads],
                us.last_user_seek AS [Last Seek],
                us.last_user_scan AS [Last Scan],
                us.last_user_lookup AS [Last Lookup],
                us.last_user_update AS [Last Write Date],
                ps.row_count AS [Row Count],
                CAST(ps.reserved_page_count * 8.0 / 1024 AS DECIMAL(12,2)) AS [Size MB]
            FROM [{safeDb}].sys.indexes i
            JOIN [{safeDb}].sys.objects o ON i.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            LEFT JOIN sys.dm_db_index_usage_stats us
                ON us.object_id = i.object_id AND us.index_id = i.index_id
                AND us.database_id = DB_ID('{safeDbStr}')
            LEFT JOIN (
                SELECT object_id, index_id,
                       SUM(row_count) AS row_count,
                       SUM(reserved_page_count) AS reserved_page_count
                FROM [{safeDb}].sys.dm_db_partition_stats
                GROUP BY object_id, index_id
            ) ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
            WHERE o.type IN ('U') AND i.type > 0 AND i.name IS NOT NULL
            ORDER BY [Total Reads] ASC, [User Updates] DESC";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var c = 0; c < reader.FieldCount; c++)
                row[reader.GetName(c)] = reader.IsDBNull(c) ? null : reader.GetValue(c);

            // Compute Last Read Date as max of seek/scan/lookup
            var dates = new[] { row["Last Seek"] as DateTime?, row["Last Scan"] as DateTime?, row["Last Lookup"] as DateTime? };
            row["Last Read Date"] = dates.Where(d => d.HasValue).DefaultIfEmpty(null).Max();

            rows.Add(row);
        }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetMissingIndexesAsync(string connectionString, string database)
    {
        var safeDb = database.Replace("]", "]]");
        var safeDbStr = database.Replace("'", "''");
        var rows = new List<Dictionary<string, object?>>();

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT
                d.statement AS [Schema.Table],
                d.equality_columns AS [Equality Columns],
                d.inequality_columns AS [Inequality Columns],
                d.included_columns AS [Included Columns],
                gs.user_seeks AS [User Seeks],
                gs.user_scans AS [User Scans],
                gs.avg_user_impact AS [Avg User Impact],
                gs.last_user_seek AS [Last Seek Date],
                CAST(gs.user_seeks * gs.avg_total_user_cost * gs.avg_user_impact / 100.0 AS DECIMAL(18,2)) AS [Score]
            FROM sys.dm_db_missing_index_details d
            JOIN sys.dm_db_missing_index_groups g ON d.index_handle = g.index_handle
            JOIN sys.dm_db_missing_index_group_stats gs ON g.index_group_handle = gs.group_handle
            WHERE d.database_id = DB_ID('{safeDbStr}')
            ORDER BY [Score] DESC";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var c = 0; c < reader.FieldCount; c++)
                row[reader.GetName(c)] = reader.IsDBNull(c) ? null : reader.GetValue(c);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetDuplicateIndexesAsync(string connectionString, string database)
    {
        var safeDb = database.Replace("]", "]]");
        var rows = new List<Dictionary<string, object?>>();

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Build index column info, then compare in C# for clarity
        var sql = $@"
            USE [{safeDb}];
            SELECT
                s.name AS SchemaName,
                o.name AS TableName,
                i.name AS IndexName,
                i.index_id,
                i.is_unique,
                ic.key_ordinal,
                ic.is_included_column,
                c.name AS ColumnName
            FROM [{safeDb}].sys.indexes i
            JOIN [{safeDb}].sys.objects o ON i.object_id = o.object_id
            JOIN [{safeDb}].sys.schemas s ON o.schema_id = s.schema_id
            JOIN [{safeDb}].sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN [{safeDb}].sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE o.type = 'U' AND i.type > 0 AND i.name IS NOT NULL
            ORDER BY s.name, o.name, i.index_id, ic.key_ordinal, ic.is_included_column";

        var indexData = new Dictionary<string, (string Schema, string Table, string IndexName, List<string> KeyCols, List<string> IncludeCols, bool IsUnique)>();

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var indexName = reader.GetString(2);
            var isIncluded = reader.GetBoolean(6);
            var colName = reader.GetString(7);
            var isUnique = reader.GetBoolean(4);

            var key = $"{schema}.{table}.{indexName}";
            if (!indexData.TryGetValue(key, out var info))
            {
                info = (schema, table, indexName, new List<string>(), new List<string>(), isUnique);
                indexData[key] = info;
            }
            if (isIncluded) info.IncludeCols.Add(colName);
            else info.KeyCols.Add(colName);
        }

        // Group by table and compare
        var byTable = indexData.Values.GroupBy(x => $"{x.Schema}.{x.Table}");
        foreach (var tableGroup in byTable)
        {
            var indexes = tableGroup.ToList();
            for (var a = 0; a < indexes.Count; a++)
            {
                for (var b = a + 1; b < indexes.Count; b++)
                {
                    var i1 = indexes[a];
                    var i2 = indexes[b];
                    var rel = ClassifyOverlap(i1.KeyCols, i2.KeyCols, i1.IncludeCols, i2.IncludeCols);
                    if (rel == null) continue;

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["Schema.Table"] = tableGroup.Key,
                        ["Index 1 Name"] = i1.IndexName,
                        ["Index 1 Key Columns"] = string.Join(", ", i1.KeyCols),
                        ["Index 1 Include Columns"] = string.Join(", ", i1.IncludeCols),
                        ["Index 2 Name"] = i2.IndexName,
                        ["Index 2 Key Columns"] = string.Join(", ", i2.KeyCols),
                        ["Index 2 Include Columns"] = string.Join(", ", i2.IncludeCols),
                        ["Relationship"] = rel,
                        ["SortOrder"] = rel == "Exact Duplicate" ? 0 : rel!.Contains("subset") ? 1 : 2
                    });
                }
            }
        }

        rows.Sort((a, b) =>
        {
            var cmp = ((int)a["SortOrder"]!).CompareTo((int)b["SortOrder"]!);
            return cmp != 0 ? cmp : string.Compare(a["Schema.Table"] as string, b["Schema.Table"] as string, StringComparison.OrdinalIgnoreCase);
        });

        return rows;
    }

    private static string? ClassifyOverlap(List<string> keys1, List<string> keys2, List<string> inc1, List<string> inc2)
    {
        var k1 = keys1.Select(c => c.ToLowerInvariant()).ToList();
        var k2 = keys2.Select(c => c.ToLowerInvariant()).ToList();

        // Exact duplicate: same key columns in same order
        if (k1.SequenceEqual(k2))
        {
            var i1 = inc1.Select(c => c.ToLowerInvariant()).OrderBy(c => c).ToList();
            var i2 = inc2.Select(c => c.ToLowerInvariant()).OrderBy(c => c).ToList();
            return i1.SequenceEqual(i2) ? "Exact Duplicate" : "Overlapping (same keys, different includes)";
        }

        // Check if one is a leading prefix of the other
        if (k1.Count < k2.Count && k2.Take(k1.Count).SequenceEqual(k1))
            return $"Index 1 is subset of Index 2";
        if (k2.Count < k1.Count && k1.Take(k2.Count).SequenceEqual(k2))
            return $"Index 2 is subset of Index 1";

        return null;
    }
}
