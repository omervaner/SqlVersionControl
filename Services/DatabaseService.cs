using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class DatabaseService
{
    private string _connectionString = "";

    public void SetConnection(ConnectionSettings settings)
    {
        _connectionString = settings.ConnectionString;
    }

    public bool IsConnected => !string.IsNullOrEmpty(_connectionString);

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetDatabasesAsync()
    {
        var databases = new List<string>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name", conn);
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            databases.Add(reader.GetString(0));
        }

        return databases;
    }

    public async Task<List<RecentChange>> GetRecentChangesAsync(string? database = null, int limit = 100)
    {
        var changes = new List<RecentChange>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT TOP (@limit)
                VersionId, ObjectName, SchemaName, ObjectType, EventType,
                ChangedBy, HostName, ChangedAt, VersionNumber
            FROM dbo.ObjectVersions
            WHERE (@database IS NULL OR DatabaseName = @database)
            ORDER BY ChangedAt DESC";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@database", (object?)database ?? DBNull.Value);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            changes.Add(new RecentChange
            {
                VersionId = reader.GetInt32(0),
                ObjectName = reader.GetString(1),
                SchemaName = reader.GetString(2),
                ObjectType = reader.GetString(3),
                EventType = reader.GetString(4),
                ChangedBy = reader.GetString(5),
                HostName = reader.GetString(6),
                ChangedAt = reader.GetDateTime(7),
                VersionNumber = reader.GetInt32(8)
            });
        }

        return changes;
    }

    public async Task<List<DatabaseObject>> GetObjectsAsync(string database)
    {
        var objects = new List<DatabaseObject>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT DatabaseName, SchemaName, ObjectName, ObjectType,
                   COUNT(*) as VersionCount, MAX(ChangedAt) as LastChanged
            FROM dbo.ObjectVersions
            WHERE DatabaseName = @database
            GROUP BY DatabaseName, SchemaName, ObjectName, ObjectType
            ORDER BY SchemaName, ObjectType, ObjectName";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@database", database);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            objects.Add(new DatabaseObject
            {
                DatabaseName = reader.GetString(0),
                SchemaName = reader.GetString(1),
                ObjectName = reader.GetString(2),
                ObjectType = reader.GetString(3),
                VersionCount = reader.GetInt32(4),
                LastChanged = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            });
        }

        return objects;
    }

    public async Task<List<ObjectVersion>> GetObjectHistoryAsync(string database, string schema, string objectName)
    {
        var versions = new List<ObjectVersion>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT VersionId, DatabaseName, SchemaName, ObjectName, ObjectType,
                   Definition, EventType, ChangedBy, HostName, IPAddress,
                   AppName, ChangedAt, VersionNumber
            FROM dbo.ObjectVersions
            WHERE DatabaseName = @database AND SchemaName = @schema AND ObjectName = @objectName
            ORDER BY VersionNumber DESC";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@database", database);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@objectName", objectName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            versions.Add(new ObjectVersion
            {
                VersionId = reader.GetInt32(0),
                DatabaseName = reader.GetString(1),
                SchemaName = reader.GetString(2),
                ObjectName = reader.GetString(3),
                ObjectType = reader.GetString(4),
                Definition = reader.IsDBNull(5) ? "" : reader.GetString(5),
                EventType = reader.GetString(6),
                ChangedBy = reader.GetString(7),
                HostName = reader.GetString(8),
                IPAddress = reader.IsDBNull(9) ? null : reader.GetString(9),
                AppName = reader.IsDBNull(10) ? null : reader.GetString(10),
                ChangedAt = reader.GetDateTime(11),
                VersionNumber = reader.GetInt32(12)
            });
        }

        return versions;
    }

    public async Task<ObjectVersion?> GetVersionAsync(int versionId)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT VersionId, DatabaseName, SchemaName, ObjectName, ObjectType,
                   Definition, EventType, ChangedBy, HostName, IPAddress,
                   AppName, ChangedAt, VersionNumber
            FROM dbo.ObjectVersions
            WHERE VersionId = @versionId";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@versionId", versionId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new ObjectVersion
            {
                VersionId = reader.GetInt32(0),
                DatabaseName = reader.GetString(1),
                SchemaName = reader.GetString(2),
                ObjectName = reader.GetString(3),
                ObjectType = reader.GetString(4),
                Definition = reader.IsDBNull(5) ? "" : reader.GetString(5),
                EventType = reader.GetString(6),
                ChangedBy = reader.GetString(7),
                HostName = reader.GetString(8),
                IPAddress = reader.IsDBNull(9) ? null : reader.GetString(9),
                AppName = reader.IsDBNull(10) ? null : reader.GetString(10),
                ChangedAt = reader.GetDateTime(11),
                VersionNumber = reader.GetInt32(12)
            };
        }

        return null;
    }

    public async Task<bool> RollbackToVersionAsync(ObjectVersion version)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so rollback works whether object exists or not
            var script = ConvertToCreateOrAlter(version.Definition);

            using var cmd = new SqlCommand(script, conn);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts CREATE PROCEDURE/FUNCTION/VIEW/TRIGGER to CREATE OR ALTER
    /// so deploy/rollback works whether object exists or not (SQL Server 2016+)
    /// </summary>
    private static string ConvertToCreateOrAlter(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return definition;

        // Skip if already has "OR ALTER"
        if (System.Text.RegularExpressions.Regex.IsMatch(definition, @"CREATE\s+OR\s+ALTER",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return definition;
        }

        // Pattern matches CREATE PROCEDURE/PROC/FUNCTION/VIEW/TRIGGER anywhere in the string
        var pattern = @"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b";
        var replacement = "CREATE OR ALTER $1";

        return System.Text.RegularExpressions.Regex.Replace(
            definition,
            pattern,
            replacement,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public async Task EnsureSchemaAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Connection not set");
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ObjectVersions')
            BEGIN
                CREATE TABLE dbo.ObjectVersions (
                    VersionId INT IDENTITY PRIMARY KEY,
                    DatabaseName NVARCHAR(128),
                    SchemaName NVARCHAR(128),
                    ObjectName NVARCHAR(128),
                    ObjectType NVARCHAR(50),
                    Definition NVARCHAR(MAX),
                    EventType NVARCHAR(50),
                    ChangedBy NVARCHAR(128),
                    HostName NVARCHAR(128),
                    IPAddress NVARCHAR(50),
                    AppName NVARCHAR(256),
                    ChangedAt DATETIME2,
                    VersionNumber INT,
                    SourceLogId INT,  -- Track which DDL_Log entry this came from
                    INDEX IX_Object (DatabaseName, SchemaName, ObjectName),
                    INDEX IX_ChangedAt (ChangedAt DESC),
                    INDEX IX_SourceLogId (SourceLogId)
                )
            END";

        using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Syncs from VMAuditDb.dbo.DDL_Log to ObjectVersions table
    /// </summary>
    public async Task<int> SyncFromDdlLogAsync(string? filterDatabase = null)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Connection not set");

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Get the last synced ID
        var getLastIdSql = "SELECT ISNULL(MAX(SourceLogId), 0) FROM dbo.ObjectVersions";
        using var getLastIdCmd = new SqlCommand(getLastIdSql, conn);
        var lastSyncedId = Convert.ToInt64(await getLastIdCmd.ExecuteScalarAsync() ?? 0);

        // Read new entries from DDL_Log (cross-database query)
        // Only include human changes (from SSMS), exclude automated/job changes
        var readLogSql = @"
            SELECT Id, DatabaseName, EventType, ObjectType, SchemaName, ObjectName,
                   CommandText, HostName, LoginName, IpAddress, ProgramName, CreatedOn
            FROM VMAuditDb.dbo.DDL_Log
            WHERE Id > @lastId
              AND (@filterDb IS NULL OR DatabaseName = @filterDb)
              -- Only stored procedures, functions, views (the stuff we care about)
              AND ObjectType IN ('PROCEDURE', 'FUNCTION', 'VIEW', 'TRIGGER',
                                 'SQL_STORED_PROCEDURE', 'SQL_SCALAR_FUNCTION',
                                 'SQL_TABLE_VALUED_FUNCTION', 'SQL_TRIGGER')
              -- Only changes from Management Studio (humans)
              AND ProgramName LIKE '%Management Studio%'
              -- Exclude temp objects
              AND ObjectName NOT LIKE '#%'
              AND ObjectName NOT LIKE 'tmp_%'
              AND ObjectName NOT LIKE 't_temp_%'
              AND ObjectName NOT LIKE 't_ft_%'
              -- Exclude stats updates
              AND EventType NOT IN ('UPDATE_STATISTICS')
            ORDER BY Id";

        using var readCmd = new SqlCommand(readLogSql, conn);
        readCmd.Parameters.AddWithValue("@lastId", lastSyncedId);
        readCmd.Parameters.AddWithValue("@filterDb", (object?)filterDatabase ?? DBNull.Value);

        var newEntries = new List<(long Id, string DbName, string EventType, string ObjType,
            string Schema, string ObjName, string Sql, string Host, string Login,
            string? Ip, string? App, DateTime CreatedOn)>();

        using (var reader = await readCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                newEntries.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? "dbo" : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7),
                    reader.IsDBNull(8) ? "" : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetDateTime(11)
                ));
            }
        }

        // Insert into ObjectVersions with calculated version numbers
        var insertSql = @"
            INSERT INTO dbo.ObjectVersions
                (DatabaseName, SchemaName, ObjectName, ObjectType, Definition, EventType,
                 ChangedBy, HostName, IPAddress, AppName, ChangedAt, VersionNumber, SourceLogId)
            VALUES
                (@db, @schema, @obj, @type, @def, @event, @user, @host, @ip, @app, @date,
                 (SELECT ISNULL(MAX(VersionNumber), 0) + 1
                  FROM dbo.ObjectVersions
                  WHERE DatabaseName = @db AND SchemaName = @schema AND ObjectName = @obj),
                 @sourceId)";

        int inserted = 0;
        foreach (var entry in newEntries)
        {
            using var insertCmd = new SqlCommand(insertSql, conn);
            insertCmd.Parameters.AddWithValue("@db", entry.DbName);
            insertCmd.Parameters.AddWithValue("@schema", entry.Schema);
            insertCmd.Parameters.AddWithValue("@obj", entry.ObjName);
            insertCmd.Parameters.AddWithValue("@type", entry.ObjType);
            insertCmd.Parameters.AddWithValue("@def", entry.Sql);
            insertCmd.Parameters.AddWithValue("@event", entry.EventType);
            insertCmd.Parameters.AddWithValue("@user", entry.Login);
            insertCmd.Parameters.AddWithValue("@host", entry.Host);
            insertCmd.Parameters.AddWithValue("@ip", (object?)entry.Ip ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@app", (object?)entry.App ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@date", entry.CreatedOn);
            insertCmd.Parameters.AddWithValue("@sourceId", entry.Id);

            await insertCmd.ExecuteNonQueryAsync();
            inserted++;
        }

        return inserted;
    }
}
