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
    /// Gets outgoing (uses) and incoming (used by) dependencies for an object.
    /// Includes definitions from sys.sql_modules so the viewer can display them immediately.
    /// </summary>
    public async Task<(List<CodeSearchResult> Uses, List<CodeSearchResult> UsedBy)> GetDependenciesAsync(
        string database, string schema, string objectName)
    {
        var uses = new List<CodeSearchResult>();
        var usedBy = new List<CodeSearchResult>();
        using var conn = new SqlConnection(_connectionString);
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
        var ddlTable = ddlSource ?? "VMAuditDb.dbo.DDL_Log";
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
    public async Task<(List<QueryResult> Results, string Messages)> ExecuteQueryAsync(
        string database, string sql, CancellationToken ct, int timeoutSeconds = 120)
        => await ExecuteQueryCoreAsync(GetConnectionStringForDatabase(database), sql, ct, timeoutSeconds);

    /// <summary>
    /// Per-tab overload: executes SQL against a specific server's database.
    /// connectionString = server-level conn string, database = target DB.
    /// </summary>
    public async Task<(List<QueryResult> Results, string Messages)> ExecuteQueryAsync(
        string connectionString, string database, string sql, CancellationToken ct, int timeoutSeconds = 120)
        => await ExecuteQueryCoreAsync(BuildConnectionString(connectionString, database), sql, ct, timeoutSeconds);

    private async Task<(List<QueryResult> Results, string Messages)> ExecuteQueryCoreAsync(
        string connStr, string sql, CancellationToken ct, int timeoutSeconds)
    {
        var results = new List<QueryResult>();
        var messages = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var conn = new SqlConnection(connStr);

        // Wire InfoMessage BEFORE OpenAsync so early PRINT messages are captured
        conn.InfoMessage += (_, e) => messages.Add(e.Message);

        await conn.OpenAsync(ct);

        // Split on GO lines
        var batches = SplitOnGo(sql);

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            ct.ThrowIfCancellationRequested();

            var batchSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var cmd = new SqlCommand(batch, conn);
                cmd.CommandTimeout = timeoutSeconds;

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

                    results.Add(new QueryResult
                    {
                        ColumnNames = colNames,
                        ColumnTypes = colTypes,
                        Rows = rows,
                        RowCount = rows.Count,
                        ExecutionTimeMs = batchSw.ElapsedMilliseconds
                    });
                } while (await reader.NextResultAsync(ct));

                // If no result sets but rows were affected, add a message
                if (reader.RecordsAffected >= 0)
                {
                    messages.Add($"({reader.RecordsAffected} rows affected)");
                }
            }
            catch (OperationCanceledException)
            {
                messages.Add("Query was cancelled by user.");
                throw;
            }
            catch (SqlException ex)
            {
                var errorMsg = ex.LineNumber > 0
                    ? $"Error (Line {ex.LineNumber}): {ex.Message}"
                    : $"Error: {ex.Message}";
                messages.Add(errorMsg);

                results.Add(new QueryResult { Error = errorMsg });
            }
        }

        sw.Stop();
        messages.Insert(0, $"Total execution time: {sw.ElapsedMilliseconds}ms");

        return (results, string.Join(Environment.NewLine, messages));
    }

    /// <summary>
    /// Splits SQL text on GO batch separators (line must be exactly GO, case-insensitive, with optional whitespace).
    /// </summary>
    private static List<string> SplitOnGo(string sql)
    {
        var batches = new List<string>();
        var lines = sql.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^GO\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                if (current.Length > 0)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
            batches.Add(current.ToString());

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

    // ── SQL Agent Jobs ─────────────────────────────────────────────

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

    // ── Table structure for Compare Databases ──────────────────────────

    public async Task<List<TableColumnInfo>> GetTableStructureAsync(string connectionString, string database)
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
}
