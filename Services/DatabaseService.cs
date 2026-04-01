using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
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

    // ── Static helper for per-tab connection strings ───────────────
    public static string BuildConnectionString(string baseConnectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    public async Task<bool> TestConnectionAsync()
        => await TestConnectionAsync(_connectionString);

    public async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetDatabasesAsync()
        => await GetDatabasesAsync(_connectionString);

    public async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var databases = new List<string>();
        using var conn = new SqlConnection(connectionString);
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

    public async Task<(bool Success, string? Error)> RollbackToVersionAsync(ObjectVersion version)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so rollback works whether object exists or not
            var script = ConvertToCreateOrAlter(version.Definition);

            using var cmd = new SqlCommand(script, conn);
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Converts CREATE PROCEDURE/FUNCTION/VIEW/TRIGGER to CREATE OR ALTER
    /// so deploy/rollback works whether object exists or not (SQL Server 2016+)
    /// </summary>
    internal static string ConvertToCreateOrAlter(string definition)
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
    /// Gets outgoing (uses) and incoming (used by) dependencies for an object.
    /// Includes definitions from sys.sql_modules so the viewer can display them immediately.
    /// </summary>
    public async Task<(List<CodeSearchResult> Uses, List<CodeSearchResult> UsedBy)> GetDependenciesAsync(
        string database, string schema, string objectName)
        => await GetDependenciesAsync(_connectionString, database, schema, objectName);

    public async Task<(List<CodeSearchResult> Uses, List<CodeSearchResult> UsedBy)> GetDependenciesAsync(
        string connectionString, string database, string schema, string objectName)
    {
        var uses = new List<CodeSearchResult>();
        var usedBy = new List<CodeSearchResult>();
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var safeName = database.Replace("]", "]]");
        var fullName = $"[{safeName}].[{schema}].[{objectName}]";

        // Outgoing: what this object references
        var usesSql = $@"
            SELECT DISTINCT
                ISNULL(d.referenced_schema_name, 'dbo') AS SchemaName,
                d.referenced_entity_name AS ObjectName,
                ISNULL(o.type_desc, '') AS ObjectType,
                ISNULL(m.definition, '') AS Definition
            FROM [{safeName}].sys.sql_expression_dependencies d
            LEFT JOIN [{safeName}].sys.objects o ON d.referenced_id = o.object_id
            LEFT JOIN [{safeName}].sys.sql_modules m ON d.referenced_id = m.object_id
            WHERE d.referencing_id = OBJECT_ID(@fullName)
              AND d.referenced_id IS NOT NULL
            ORDER BY d.referenced_entity_name";

        using (var cmd = new SqlCommand(usesSql, conn))
        {
            cmd.CommandTimeout = 30;
            cmd.Parameters.AddWithValue("@fullName", fullName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                uses.Add(new CodeSearchResult
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Definition = reader.IsDBNull(3) ? "" : reader.GetString(3)
                });
            }
        }

        // Incoming: what references this object
        var usedBySql = $@"
            SELECT DISTINCT
                s.name AS SchemaName,
                o.name AS ObjectName,
                o.type_desc AS ObjectType,
                ISNULL(m.definition, '') AS Definition
            FROM [{safeName}].sys.sql_expression_dependencies d
            JOIN [{safeName}].sys.objects o ON d.referencing_id = o.object_id
            JOIN [{safeName}].sys.schemas s ON o.schema_id = s.schema_id
            LEFT JOIN [{safeName}].sys.sql_modules m ON d.referencing_id = m.object_id
            WHERE d.referenced_id = OBJECT_ID(@fullName)
            ORDER BY o.name";

        using (var cmd = new SqlCommand(usedBySql, conn))
        {
            cmd.CommandTimeout = 30;
            cmd.Parameters.AddWithValue("@fullName", fullName);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                usedBy.Add(new CodeSearchResult
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Definition = reader.IsDBNull(3) ? "" : reader.GetString(3)
                });
            }
        }

        return (uses, usedBy);
    }

    /// <summary>
    /// Searches live object definitions in sys.sql_modules for the given database.
    /// LIKE mode filters server-side; regex mode fetches all for client-side filtering.
    /// </summary>
    public async Task<List<CodeSearchResult>> SearchObjectDefinitionsAsync(
        string database, string searchTerm, bool useRegex)
    {
        var results = new List<CodeSearchResult>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Sanitize database name for safe use in dynamic SQL (bracket-quote it)
        var safeName = database.Replace("]", "]]");

        var sql = $@"
            SELECT
                s.name AS SchemaName,
                o.name AS ObjectName,
                o.type_desc AS ObjectType,
                m.definition
            FROM [{safeName}].sys.sql_modules m
            JOIN [{safeName}].sys.objects o ON m.object_id = o.object_id
            JOIN [{safeName}].sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.is_ms_shipped = 0"
            + (useRegex ? "" : "\n              AND m.definition LIKE @searchTerm")
            + "\n            ORDER BY o.name";

        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 60;

        if (!useRegex)
        {
            cmd.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new CodeSearchResult
            {
                SchemaName = reader.GetString(0),
                ObjectName = reader.GetString(1),
                ObjectType = reader.GetString(2),
                Definition = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }

        return results;
    }

    private static string SafeDdlTableRef(string ddlSource)
    {
        var parts = ddlSource.Split('.', 3);
        if (parts.Length == 3)
            return $"[{parts[0].Replace("]", "]]")}].[{parts[1].Replace("]", "]]")}].[{parts[2].Replace("]", "]]")}]";
        if (parts.Length == 2)
            return $"[{parts[0].Replace("]", "]]")}].dbo.[{parts[1].Replace("]", "]]")}]";
        throw new ArgumentException("DDL audit source must be in Database.Schema.Table or Database.Table format");
    }

    /// <summary>
    /// Syncs from DDL audit log to ObjectVersions table.
    /// ddlSource should be "DatabaseName.dbo.TableName" (fully qualified).
    /// </summary>
    public async Task<int> SyncFromDdlLogAsync(string? filterDatabase = null, string? ddlSource = null)
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
        var ddlTable = SafeDdlTableRef(ddlSource ?? "VMAuditDb.dbo.DDL_Log");
        var readLogSql = $@"
            SELECT Id, DatabaseName, EventType, ObjectType, SchemaName, ObjectName,
                   CommandText, HostName, LoginName, IpAddress, ProgramName, CreatedOn
            FROM {ddlTable}
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

    /// <summary>
    /// Generates an estimated execution plan for a stored procedure using SET SHOWPLAN_XML ON.
    /// Falls back to cached plan from DMVs if the proc requires parameters.
    /// </summary>
    public async Task<string?> GetEstimatedPlanAsync(string database, string schema, string objectName)
    {
        try
        {
            var safeDb = database.Replace("]", "]]");
            var safeSchema = schema.Replace("]", "]]");
            var safeName = objectName.Replace("]", "]]");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // SET SHOWPLAN_XML ON generates the estimated plan WITHOUT executing the proc
            using var onCmd = new SqlCommand("SET SHOWPLAN_XML ON", conn);
            await onCmd.ExecuteNonQueryAsync();

            try
            {
                var execSql = $"EXEC [{safeDb}].[{safeSchema}].[{safeName}]";
                using var planCmd = new SqlCommand(execSql, conn);
                planCmd.CommandTimeout = 30;
                var xml = (string?)await planCmd.ExecuteScalarAsync();
                return xml;
            }
            finally
            {
                using var offCmd = new SqlCommand("SET SHOWPLAN_XML OFF", conn);
                await offCmd.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Fallback: fetch cached plan from DMVs (for procs requiring parameters)
            return await GetCachedPlanAsync(database, schema, objectName);
        }
    }

    /// <summary>
    /// Fetches a cached execution plan from sys.dm_exec_procedure_stats for procs
    /// that can't generate an estimated plan (e.g. require parameters).
    /// </summary>
    private async Task<string?> GetCachedPlanAsync(string database, string schema, string objectName)
    {
        try
        {
            var safeDb = database.Replace("]", "]]");
            var sql = $@"
                SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX))
                FROM [{safeDb}].sys.dm_exec_procedure_stats ps
                CROSS APPLY sys.dm_exec_query_plan(ps.plan_handle) qp
                WHERE ps.database_id = DB_ID(@database)
                  AND OBJECT_NAME(ps.object_id, ps.database_id) = @objectName
                  AND OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id) = @schema
                  AND qp.query_plan IS NOT NULL
                ORDER BY ps.last_execution_time DESC";

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 30;
            cmd.Parameters.AddWithValue("@database", database);
            cmd.Parameters.AddWithValue("@objectName", objectName);
            cmd.Parameters.AddWithValue("@schema", schema);

            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses plan XML and runs PlanViewer.Core's 30 analysis rules (warnings, missing indexes, etc.)
    /// </summary>
    public static (ParsedPlan? Plan, string? Error) ParseAndAnalyzePlan(string planXml)
    {
        try
        {
            var plan = ShowPlanParser.Parse(planXml);
            PlanAnalyzer.Analyze(plan);
            return (plan, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Builds a connection string targeting a specific database, using the current server credentials.
    /// Used by Query Editor so it runs on a dedicated connection.
    /// </summary>
    public string GetConnectionStringForDatabase(string database)
        => BuildConnectionString(_connectionString, database);

    /// <summary>
    /// Executes one or more SQL batches (split on GO) against the given database.
    /// Uses a dedicated connection. Wires InfoMessage BEFORE OpenAsync so early PRINTs are captured.
    /// </summary>
    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        string database, string sql, CancellationToken ct, int timeoutSeconds = 120)
        => await ExecuteQueryCoreAsync(GetConnectionStringForDatabase(database), sql, ct, timeoutSeconds);

    /// <summary>
    /// Per-tab overload: executes SQL against a specific server's database.
    /// connectionString = server-level conn string, database = target DB.
    /// </summary>
    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        string connectionString, string database, string sql, CancellationToken ct, int timeoutSeconds = 120)
        => await ExecuteQueryCoreAsync(BuildConnectionString(connectionString, database), sql, ct, timeoutSeconds);

    /// <summary>
    /// Execute a query with XE trace (Mode 1 — Quick Trace).
    /// Uses a dedicated connection so SPID is guaranteed to match the XE filter.
    /// The XE session is always cleaned up, even on failure or cancellation.
    /// </summary>
    public async Task<(QueryExecutionResult Result, List<SqlVersionControl.Models.TraceEvent> TraceEvents)>
        ExecuteWithTraceAsync(string connectionString, string database, string sql,
            CancellationToken ct, TraceService traceService, int timeoutSeconds = 120)
    {
        var dbConnStr = BuildConnectionString(connectionString, database);
        var results = new List<QueryResult>();
        var messages = new List<QueryMessage>();
        var traceEvents = new List<SqlVersionControl.Models.TraceEvent>();
        var totalRowsAffected = 0;
        var hasErrors = false;
        var errorCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Dedicated connection for the query (stays open so SPID is stable)
        using var queryConn = new SqlConnection(dbConnStr);
        queryConn.InfoMessage += (_, e) => messages.Add(new QueryMessage { Type = MessageType.Print, Text = e.Message });
        await queryConn.OpenAsync(ct);

        // Get SPID from this connection
        int spid;
        using (var spidCmd = new SqlCommand("SELECT @@SPID", queryConn))
            spid = Convert.ToInt32(await spidCmd.ExecuteScalarAsync(ct));

        // Start XE session filtered to this SPID (uses a separate connection)
        string? sessionName = null;
        try
        {
            sessionName = await traceService.StartTraceAsync(connectionString, new SqlVersionControl.Models.TraceOptions
            {
                SpidFilter = spid,
                CaptureStatements = true
            });

            // Execute the query on the dedicated connection
            var batches = SplitOnGo(sql);
            foreach (var (batch, batchStartLine) in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                ct.ThrowIfCancellationRequested();

                var batchSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var cmd = new SqlCommand(batch, queryConn);
                    cmd.CommandTimeout = timeoutSeconds;

                    cmd.StatementCompleted += (_, sce) =>
                    {
                        if (sce.RecordCount >= 0)
                        {
                            totalRowsAffected += sce.RecordCount;
                            messages.Add(new QueryMessage
                            {
                                Type = MessageType.RowCount,
                                Text = $"({sce.RecordCount} row(s) affected)"
                            });
                        }
                    };

                    await using var reg = ct.Register(() =>
                    {
                        try { cmd.Cancel(); } catch { }
                    });

                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    do
                    {
                        var colCount = reader.FieldCount;
                        if (colCount == 0) continue;

                        var colNames = new string[colCount];
                        var colTypes = new Type[colCount];
                        for (int i = 0; i < colCount; i++)
                        {
                            colNames[i] = reader.GetName(i);
                            colTypes[i] = reader.GetFieldType(i);
                        }

                        var rows = new List<object?[]>();
                        while (await reader.ReadAsync(ct))
                        {
                            var row = new object?[colCount];
                            for (int i = 0; i < colCount; i++)
                                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            rows.Add(row);
                        }

                        var qr = new QueryResult
                        {
                            ColumnNames = colNames,
                            ColumnTypes = colTypes,
                            Rows = rows,
                            RowCount = rows.Count,
                            ExecutionTimeMs = batchSw.ElapsedMilliseconds
                        };
                        results.Add(qr);

                        messages.Add(new QueryMessage
                        {
                            Type = MessageType.RowCount,
                            Text = $"({qr.RowCount} row(s) affected)"
                        });
                    } while (await reader.NextResultAsync(ct));
                }
                catch (OperationCanceledException)
                {
                    messages.Add(new QueryMessage { Type = MessageType.Info, Text = "Query was cancelled by user." });
                    throw;
                }
                catch (SqlException ex)
                {
                    hasErrors = true;
                    errorCount++;

                    foreach (SqlError err in ex.Errors)
                    {
                        var scriptLine = err.LineNumber > 0 ? batchStartLine + err.LineNumber - 1 : -1;
                        var header = $"Msg {err.Number}, Level {err.Class}, State {err.State}, Line {scriptLine}";
                        messages.Add(new QueryMessage
                        {
                            Type = MessageType.Error,
                            Text = $"{header}\n{err.Message}",
                            LineNumber = scriptLine
                        });
                    }

                    var primaryError = ex.Errors[0];
                    var primaryLine = primaryError.LineNumber > 0 ? batchStartLine + primaryError.LineNumber - 1 : -1;
                    results.Add(new QueryResult
                    {
                        Error = $"Msg {primaryError.Number}, Level {primaryError.Class}, State {primaryError.State}, Line {primaryLine}: {primaryError.Message}"
                    });
                }
            }

            sw.Stop();

            if (!hasErrors && results.Count == 0 && totalRowsAffected == 0
                && !messages.Any(m => m.Type == MessageType.RowCount))
            {
                messages.Add(new QueryMessage
                {
                    Type = MessageType.Info,
                    Text = "Commands completed successfully."
                });
            }

            messages.Add(new QueryMessage
            {
                Type = MessageType.Timing,
                Text = $"Total execution time: {sw.ElapsedMilliseconds}ms"
            });

            // Small delay to let XE flush the ring buffer
            await Task.Delay(200, CancellationToken.None);

            // Read trace events
            traceEvents = await traceService.ReadEventsAsync(connectionString, sessionName);
            messages.Add(new QueryMessage
            {
                Type = MessageType.Info,
                Text = $"Trace captured {traceEvents.Count} statement(s)"
            });
        }
        finally
        {
            // Always clean up the XE session
            if (sessionName != null)
                await traceService.StopTraceAsync(connectionString, sessionName);
        }

        return (new QueryExecutionResult
        {
            Results = results,
            Messages = messages,
            TotalRowsAffected = totalRowsAffected,
            HasErrors = hasErrors,
            ErrorCount = errorCount
        }, traceEvents);
    }

    private async Task<QueryExecutionResult> ExecuteQueryCoreAsync(
        string connStr, string sql, CancellationToken ct, int timeoutSeconds)
    {
        var results = new List<QueryResult>();
        var messages = new List<QueryMessage>();
        var totalRowsAffected = 0;
        var hasErrors = false;
        var errorCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var conn = new SqlConnection(connStr);

        // Wire InfoMessage BEFORE OpenAsync so early PRINT messages are captured
        conn.InfoMessage += (_, e) => messages.Add(new QueryMessage { Type = MessageType.Print, Text = e.Message });

        await conn.OpenAsync(ct);

        // Split on GO lines
        var batches = SplitOnGo(sql);

        foreach (var (batch, batchStartLine) in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            ct.ThrowIfCancellationRequested();

            var batchSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var cmd = new SqlCommand(batch, conn);
                cmd.CommandTimeout = timeoutSeconds;

                // Track per-statement row counts (fires even if a later statement errors)
                cmd.StatementCompleted += (_, sce) =>
                {
                    if (sce.RecordCount >= 0)
                    {
                        totalRowsAffected += sce.RecordCount;
                        messages.Add(new QueryMessage
                        {
                            Type = MessageType.RowCount,
                            Text = $"({sce.RecordCount} row(s) affected)"
                        });
                    }
                };

                // Register cancellation to call SqlCommand.Cancel()
                await using var reg = ct.Register(() =>
                {
                    try { cmd.Cancel(); } catch { /* already disposed */ }
                });

                using var reader = await cmd.ExecuteReaderAsync(ct);

                do
                {
                    var colCount = reader.FieldCount;
                    if (colCount == 0) continue; // non-SELECT batch (UPDATE/INSERT/etc.)

                    var colNames = new string[colCount];
                    var colTypes = new Type[colCount];
                    for (int i = 0; i < colCount; i++)
                    {
                        colNames[i] = reader.GetName(i);
                        colTypes[i] = reader.GetFieldType(i);
                    }

                    var rows = new List<object?[]>();
                    while (await reader.ReadAsync(ct))
                    {
                        var row = new object?[colCount];
                        for (int i = 0; i < colCount; i++)
                        {
                            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        rows.Add(row);
                    }

                    var qr = new QueryResult
                    {
                        ColumnNames = colNames,
                        ColumnTypes = colTypes,
                        Rows = rows,
                        RowCount = rows.Count,
                        ExecutionTimeMs = batchSw.ElapsedMilliseconds
                    };
                    results.Add(qr);

                    // SELECT row count in Messages (SSMS shows this too)
                    messages.Add(new QueryMessage
                    {
                        Type = MessageType.RowCount,
                        Text = $"({qr.RowCount} row(s) affected)"
                    });
                } while (await reader.NextResultAsync(ct));
            }
            catch (OperationCanceledException)
            {
                messages.Add(new QueryMessage { Type = MessageType.Info, Text = "Query was cancelled by user." });
                throw;
            }
            catch (SqlException ex)
            {
                hasErrors = true;
                errorCount++;

                // SSMS-style: iterate SqlException.Errors for full metadata
                foreach (SqlError err in ex.Errors)
                {
                    var scriptLine = err.LineNumber > 0 ? batchStartLine + err.LineNumber - 1 : -1;
                    var header = $"Msg {err.Number}, Level {err.Class}, State {err.State}, Line {scriptLine}";
                    messages.Add(new QueryMessage
                    {
                        Type = MessageType.Error,
                        Text = $"{header}\n{err.Message}",
                        LineNumber = scriptLine
                    });
                }

                // Still add a QueryResult with error for the result tab indicator
                var primaryError = ex.Errors[0];
                var primaryLine = primaryError.LineNumber > 0 ? batchStartLine + primaryError.LineNumber - 1 : -1;
                results.Add(new QueryResult
                {
                    Error = $"Msg {primaryError.Number}, Level {primaryError.Class}, State {primaryError.State}, Line {primaryLine}: {primaryError.Message}"
                });
            }
        }

        sw.Stop();

        // "Commands completed successfully." for DDL with no output (SSMS behavior)
        if (!hasErrors && results.Count == 0 && totalRowsAffected == 0
            && !messages.Any(m => m.Type == MessageType.RowCount))
        {
            messages.Add(new QueryMessage
            {
                Type = MessageType.Info,
                Text = "Commands completed successfully."
            });
        }

        messages.Add(new QueryMessage
        {
            Type = MessageType.Timing,
            Text = $"Total execution time: {sw.ElapsedMilliseconds}ms"
        });

        return new QueryExecutionResult
        {
            Results = results,
            Messages = messages,
            TotalRowsAffected = totalRowsAffected,
            HasErrors = hasErrors,
            ErrorCount = errorCount
        };
    }

    /// <summary>
    /// Splits SQL text on GO batch separators. Returns (batchSql, startLineNumber) tuples
    /// where startLineNumber is 1-based line offset in the original script.
    /// </summary>
    private static List<(string Sql, int StartLine)> SplitOnGo(string sql)
    {
        var batches = new List<(string Sql, int StartLine)>();
        var lines = sql.Split('\n');
        var current = new System.Text.StringBuilder();
        int batchStartLine = 1; // 1-based

        for (int i = 0; i < lines.Length; i++)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i].Trim(), @"^GO\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (current.Length > 0)
                {
                    batches.Add((current.ToString(), batchStartLine));
                    current.Clear();
                }
                batchStartLine = i + 2; // next line is start of next batch (1-based)
            }
            else
            {
                if (current.Length == 0)
                    batchStartLine = i + 1; // 1-based
                current.AppendLine(lines[i]);
            }
        }

        if (current.Length > 0)
            batches.Add((current.ToString(), batchStartLine));

        return batches;
    }

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

    // ── Activity Monitor — Active Sessions ──────────────────────────

    public async Task<int> GetCurrentSpidAsync(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT @@SPID", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<Dictionary<string, object?>>> GetActiveSessionsAsync(string connectionString)
    {
        var results = new List<Dictionary<string, object?>>();
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT
                s.session_id AS [Session ID],
                s.login_name AS [Login],
                DB_NAME(s.database_id) AS [Database],
                s.status AS [Session Status],
                r.status AS [Request Status],
                r.command AS [Command],
                r.wait_type AS [Wait Type],
                r.wait_time AS [Wait Time (ms)],
                r.blocking_session_id AS [Blocking Session],
                r.cpu_time AS [CPU (ms)],
                r.reads AS [Reads],
                r.writes AS [Writes],
                r.logical_reads AS [Logical Reads],
                DATEDIFF(SECOND, r.start_time, GETDATE()) AS [Elapsed (s)],
                r.percent_complete AS [% Complete],
                r.open_transaction_count AS [Open Trans],
                s.host_name AS [Host],
                s.program_name AS [Program],
                t.text AS [Query Text],
                SUBSTRING(t.text, (r.statement_start_offset/2)+1,
                    ((CASE r.statement_end_offset
                        WHEN -1 THEN DATALENGTH(t.text)
                        ELSE r.statement_end_offset
                    END - r.statement_start_offset)/2)+1) AS [Current Statement]
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1
            ORDER BY r.cpu_time DESC";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            foreach (var col in columns)
                row[col] = reader.IsDBNull(reader.GetOrdinal(col)) ? null : reader.GetValue(reader.GetOrdinal(col));
            results.Add(row);
        }

        return results;
    }

    public async Task KillSessionAsync(string connectionString, int sessionId)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        // KILL cannot use parameters — use string formatting but sessionId is always int (safe)
        using var cmd = new SqlCommand($"KILL {sessionId}", conn) { CommandTimeout = 30 };
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Activity Monitor — Jobs Dashboard ─────────────────────────

    public async Task<List<Dictionary<string, object?>>> GetJobsDashboardAsync(string connectionString)
    {
        var results = new List<Dictionary<string, object?>>();
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT
                j.name AS JobName,
                j.enabled AS IsEnabled,
                cat.name AS Category,
                CASE
                    WHEN ja.run_requested_date IS NOT NULL AND ja.stop_execution_date IS NULL THEN 'Executing'
                    ELSE 'Idle'
                END AS CurrentStatus,
                CASE ISNULL(h.run_status, -1)
                    WHEN 0 THEN 'Failed'
                    WHEN 1 THEN 'Succeeded'
                    WHEN 2 THEN 'Retry'
                    WHEN 3 THEN 'Cancelled'
                    WHEN 4 THEN 'In Progress'
                    ELSE 'Unknown'
                END AS LastRunOutcome,
                CASE WHEN h.run_date IS NOT NULL
                     THEN msdb.dbo.agent_datetime(h.run_date, h.run_time)
                     ELSE NULL
                END AS LastRunDate,
                CASE WHEN h.run_duration IS NOT NULL
                     THEN (h.run_duration / 10000) * 3600 + ((h.run_duration / 100) % 100) * 60 + (h.run_duration % 100)
                     ELSE NULL
                END AS LastDurationSec,
                ja.next_scheduled_run_date AS NextRunDate,
                ISNULL(sched.name, '') AS ScheduleName,
                ISNULL(sched.freq_type, 0) AS FreqType,
                ISNULL(sched.freq_interval, 0) AS FreqInterval,
                ISNULL(sched.freq_subday_type, 0) AS FreqSubdayType,
                ISNULL(sched.freq_subday_interval, 0) AS FreqSubdayInterval,
                ISNULL(sched.active_start_time, 0) AS ActiveStartTime,
                j.description AS JobDescription,
                (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps js4 WHERE js4.job_id = j.job_id) AS StepCount
            FROM msdb.dbo.sysjobs j
            LEFT JOIN msdb.dbo.syscategories cat ON j.category_id = cat.category_id
            LEFT JOIN msdb.dbo.sysjobactivity ja
                ON ja.job_id = j.job_id
                AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            LEFT JOIN msdb.dbo.sysjobhistory h
                ON h.job_id = j.job_id AND h.step_id = 0
                AND h.instance_id = (SELECT MAX(h2.instance_id) FROM msdb.dbo.sysjobhistory h2
                                     WHERE h2.job_id = j.job_id AND h2.step_id = 0)
            LEFT JOIN msdb.dbo.sysjobschedules js ON j.job_id = js.job_id
            LEFT JOIN msdb.dbo.sysschedules sched ON js.schedule_id = sched.schedule_id
            ORDER BY j.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            foreach (var col in columns)
                row[col] = reader.IsDBNull(reader.GetOrdinal(col)) ? null : reader.GetValue(reader.GetOrdinal(col));
            results.Add(row);
        }

        return results;
    }

    public async Task StopJobAsync(string connectionString, string jobName)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_stop_job", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task EnableDisableJobAsync(string connectionString, string jobName, bool enabled)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_update_job", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Activity Monitor — Job CRUD ──────────────────────────────

    public async Task UpdateJobAsync(string connectionString, string jobName,
        string? newName = null, string? description = null, bool? enabled = null, int? categoryId = null)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_update_job", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        if (newName != null) cmd.Parameters.AddWithValue("@new_name", newName);
        if (description != null) cmd.Parameters.AddWithValue("@description", description);
        if (enabled.HasValue) cmd.Parameters.AddWithValue("@enabled", enabled.Value ? 1 : 0);
        if (categoryId.HasValue) cmd.Parameters.AddWithValue("@category_id", categoryId.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(int CategoryId, string Name)>> GetJobCategoriesAsync(string connectionString)
    {
        var results = new List<(int, string)>();
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("SELECT category_id, name FROM msdb.dbo.syscategories ORDER BY name", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetInt32(0), reader.GetString(1)));

        return results;
    }

    public async Task<List<(int StepId, string StepName, string Subsystem, string Command,
        string OnSuccessAction, string OnFailureAction)>> GetJobStepsDetailedAsync(
        string connectionString, string jobName)
    {
        var results = new List<(int, string, string, string, string, string)>();
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT js.step_id, js.step_name, js.subsystem, js.command,
                   CASE js.on_success_action
                       WHEN 1 THEN 'Quit with success'
                       WHEN 2 THEN 'Quit with failure'
                       WHEN 3 THEN 'Go to next step'
                       WHEN 4 THEN 'Go to step ' + CAST(js.on_success_step_id AS VARCHAR)
                       ELSE 'Unknown'
                   END AS OnSuccessAction,
                   CASE js.on_fail_action
                       WHEN 1 THEN 'Quit with success'
                       WHEN 2 THEN 'Quit with failure'
                       WHEN 3 THEN 'Go to next step'
                       WHEN 4 THEN 'Go to step ' + CAST(js.on_fail_step_id AS VARCHAR)
                       ELSE 'Unknown'
                   END AS OnFailureAction
            FROM msdb.dbo.sysjobsteps js
            JOIN msdb.dbo.sysjobs j ON js.job_id = j.job_id
            WHERE j.name = @jobName
            ORDER BY js.step_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@jobName", jobName);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));

        return results;
    }

    public async Task AddJobStepAsync(string connectionString, string jobName,
        string stepName, string subsystem, string command, string database)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_add_jobstep", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@step_name", stepName);
        cmd.Parameters.AddWithValue("@subsystem", subsystem);
        cmd.Parameters.AddWithValue("@command", command);
        cmd.Parameters.AddWithValue("@database_name", database);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateJobStepAsync(string connectionString, string jobName,
        int stepId, string stepName, string command)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_update_jobstep", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@step_id", stepId);
        cmd.Parameters.AddWithValue("@step_name", stepName);
        cmd.Parameters.AddWithValue("@command", command);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteJobStepAsync(string connectionString, string jobName, int stepId)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_delete_jobstep", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@step_id", stepId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(int ScheduleId, string Name, int FreqType, int FreqInterval,
        int FreqSubdayType, int FreqSubdayInterval, int ActiveStartTime)?> GetJobScheduleAsync(
        string connectionString, string jobName)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT TOP 1 sched.schedule_id, sched.name, sched.freq_type, sched.freq_interval,
                   sched.freq_subday_type, sched.freq_subday_interval, sched.active_start_time
            FROM msdb.dbo.sysjobschedules js
            JOIN msdb.dbo.sysschedules sched ON js.schedule_id = sched.schedule_id
            JOIN msdb.dbo.sysjobs j ON js.job_id = j.job_id
            WHERE j.name = @jobName";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@jobName", jobName);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return (reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));

        return null;
    }

    public async Task AddJobScheduleAsync(string connectionString, string jobName,
        string scheduleName, int freqType, int freqInterval, int freqSubdayType,
        int freqSubdayInterval, int activeStartTime)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_add_jobschedule", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@name", scheduleName);
        cmd.Parameters.AddWithValue("@freq_type", freqType);
        cmd.Parameters.AddWithValue("@freq_interval", freqInterval);
        cmd.Parameters.AddWithValue("@freq_subday_type", freqSubdayType);
        cmd.Parameters.AddWithValue("@freq_subday_interval", freqSubdayInterval);
        cmd.Parameters.AddWithValue("@active_start_time", activeStartTime);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateJobScheduleAsync(string connectionString, string jobName,
        int scheduleId, int freqType, int freqInterval, int freqSubdayType,
        int freqSubdayInterval, int activeStartTime)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_update_schedule", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@schedule_id", scheduleId);
        cmd.Parameters.AddWithValue("@freq_type", freqType);
        cmd.Parameters.AddWithValue("@freq_interval", freqInterval);
        cmd.Parameters.AddWithValue("@freq_subday_type", freqSubdayType);
        cmd.Parameters.AddWithValue("@freq_subday_interval", freqSubdayInterval);
        cmd.Parameters.AddWithValue("@active_start_time", activeStartTime);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteJobScheduleAsync(string connectionString, string jobName, int scheduleId)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_detach_schedule", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
        cmd.Parameters.AddWithValue("@schedule_id", scheduleId);
        cmd.Parameters.AddWithValue("@delete_unused_schedule", 1);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── SQL Agent Jobs (OE) ───────────────────────────────────────

    private static string ToMsdbConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.InitialCatalog = "msdb";
        return builder.ConnectionString;
    }

    public async Task<List<(string Name, bool Enabled, string LastOutcome, DateTime? LastRunDate)>> GetJobsAsync()
        => await GetJobsAsync(_connectionString);

    public async Task<List<(string Name, bool Enabled, string LastOutcome, DateTime? LastRunDate)>> GetJobsAsync(string connectionString)
    {
        var results = new List<(string, bool, string, DateTime?)>();

        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT j.name,
                   j.enabled,
                   ISNULL(h.run_status, -1),
                   ja.last_executed_step_date
            FROM msdb.dbo.sysjobs j
            LEFT JOIN msdb.dbo.sysjobactivity ja
                ON ja.job_id = j.job_id
                AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            LEFT JOIN msdb.dbo.sysjobhistory h
                ON h.job_id = j.job_id AND h.step_id = 0
                AND h.instance_id = (
                    SELECT MAX(h2.instance_id)
                    FROM msdb.dbo.sysjobhistory h2
                    WHERE h2.job_id = j.job_id AND h2.step_id = 0)
            ORDER BY j.name";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var enabled = Convert.ToInt32(reader.GetValue(1)) == 1;
            var runStatus = Convert.ToInt32(reader.GetValue(2));
            var lastOutcome = runStatus switch
            {
                0 => "Failed",
                1 => "Success",
                2 => "Retry",
                3 => "Cancelled",
                4 => "Running",
                _ => "Unknown"
            };
            DateTime? lastRun = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            results.Add((name, enabled, lastOutcome, lastRun));
        }

        return results;
    }

    public async Task<List<(int StepId, string StepName, string Subsystem, string Command)>> GetJobStepsAsync(string jobName)
        => await GetJobStepsAsync(_connectionString, jobName);

    public async Task<List<(int StepId, string StepName, string Subsystem, string Command)>> GetJobStepsAsync(
        string connectionString, string jobName)
    {
        var results = new List<(int, string, string, string)>();

        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT js.step_id, js.step_name, js.subsystem, js.command
            FROM msdb.dbo.sysjobsteps js
            JOIN msdb.dbo.sysjobs j ON js.job_id = j.job_id
            WHERE j.name = @jobName
            ORDER BY js.step_id";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@jobName", jobName);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        return results;
    }

    public async Task<List<(int RunStatus, DateTime RunDate, int DurationSeconds, string Message)>> GetJobHistoryAsync(string jobName)
        => await GetJobHistoryAsync(_connectionString, jobName);

    public async Task<List<(int RunStatus, DateTime RunDate, int DurationSeconds, string Message)>> GetJobHistoryAsync(
        string connectionString, string jobName)
    {
        var results = new List<(int, DateTime, int, string)>();

        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        var sql = @"
            SELECT TOP 10
                   h.run_status,
                   msdb.dbo.agent_datetime(h.run_date, h.run_time),
                   (h.run_duration / 10000) * 3600
                     + ((h.run_duration / 100) % 100) * 60
                     + (h.run_duration % 100),
                   h.message
            FROM msdb.dbo.sysjobhistory h
            JOIN msdb.dbo.sysjobs j ON h.job_id = j.job_id
            WHERE j.name = @jobName AND h.step_id = 0
            ORDER BY h.instance_id DESC";

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@jobName", jobName);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetInt32(0), reader.GetDateTime(1), reader.GetInt32(2),
                          reader.IsDBNull(3) ? "" : reader.GetString(3)));

        return results;
    }

    public async Task StartJobAsync(string jobName)
        => await StartJobAsync(_connectionString, jobName);

    public async Task StartJobAsync(string connectionString, string jobName)
    {
        using var conn = new SqlConnection(ToMsdbConnection(connectionString));
        await conn.OpenAsync();

        using var cmd = new SqlCommand("msdb.dbo.sp_start_job", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.AddWithValue("@job_name", jobName);
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
