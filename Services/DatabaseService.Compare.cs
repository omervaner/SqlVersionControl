using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
    // ── Table structure for Compare Databases ──────────────────────────

    public static async Task<List<TableColumnInfo>> GetTableStructureAsync(string connectionString, string database)
    {
        var results = new List<TableColumnInfo>();
        var safeDb = database.Replace("]", "]]");

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = $@"
            SELECT
                c.TABLE_SCHEMA,
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                CAST(sc.max_length AS INT) AS MaxLength,
                sc.precision,
                sc.scale,
                CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IsNullable,
                dc.name AS DefaultConstraintName,
                dc.definition AS DefaultDefinition
            FROM [{safeDb}].INFORMATION_SCHEMA.COLUMNS c
            JOIN [{safeDb}].INFORMATION_SCHEMA.TABLES t
              ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            JOIN [{safeDb}].sys.columns sc
              ON sc.object_id = OBJECT_ID('[{safeDb}].' + QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME))
              AND sc.name = c.COLUMN_NAME
            LEFT JOIN [{safeDb}].sys.default_constraints dc
              ON dc.parent_object_id = sc.object_id AND dc.parent_column_id = sc.column_id
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new TableColumnInfo
            {
                Schema = reader.GetString(0),
                TableName = reader.GetString(1),
                ColumnName = reader.GetString(2),
                DataType = reader.GetString(3),
                MaxLength = reader.GetInt32(4),
                Precision = reader.GetByte(5),
                Scale = reader.GetByte(6),
                IsNullable = reader.GetInt32(7) == 1,
                DefaultConstraintName = reader.IsDBNull(8) ? null : reader.GetString(8),
                DefaultDefinition = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return results;
    }
}
