using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public partial class DatabaseService
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
    /// Generates an estimated execution plan for arbitrary SQL using SET SHOWPLAN_XML ON.
    /// Uses the given connection string + database. Returns raw plan XML or null.
    /// </summary>
    public async Task<string?> GetEstimatedPlanForQueryAsync(string connectionString, string database, string sql)
    {
        try
        {
            var connStr = BuildConnectionString(connectionString, database);
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            using var onCmd = new SqlCommand("SET SHOWPLAN_XML ON", conn);
            await onCmd.ExecuteNonQueryAsync();

            try
            {
                // SHOWPLAN_XML returns one result set per batch statement — collect all
                using var planCmd = new SqlCommand(sql, conn);
                planCmd.CommandTimeout = 30;
                using var reader = await planCmd.ExecuteReaderAsync();

                string? xml = null;
                while (await reader.ReadAsync())
                {
                    xml = reader.GetString(0);
                }
                // If multiple result sets (multiple statements), read remaining
                while (await reader.NextResultAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        // Take the last plan XML (covers multi-statement batches)
                        xml = reader.GetString(0);
                    }
                }
                return xml;
            }
            finally
            {
                using var offCmd = new SqlCommand("SET SHOWPLAN_XML OFF", conn);
                await offCmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate execution plan: {ex.Message}", ex);
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
    /// Detect if a table has an identity column, and return its name (or null).
    /// </summary>
    public static async Task<string?> GetIdentityColumnAsync(string connectionString, string schema, string table)
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
}
