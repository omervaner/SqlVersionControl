using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class TraceService
{
    /// <summary>
    /// Check if the current login has ALTER ANY EVENT SESSION permission.
    /// </summary>
    public async Task<bool> HasTracePermissionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION')", conn);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Create and start an XE session. Returns the session name.
    /// </summary>
    public async Task<string> StartTraceAsync(string connectionString, TraceOptions options)
    {
        var sessionName = $"LookoutTrace_{Guid.NewGuid():N}";

        var eventDefs = options.CaptureStatements
            ? BuildStatementEventDef(options)
            : BuildBatchRpcEventDefs(options);

        var createSql = $@"
CREATE EVENT SESSION [{sessionName}] ON SERVER
{eventDefs}
ADD TARGET package0.ring_buffer(SET max_memory = 8192)
WITH (
    MAX_DISPATCH_LATENCY = 1 SECONDS,
    EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
    TRACK_CAUSALITY = OFF
)";

        var startSql = $"ALTER EVENT SESSION [{sessionName}] ON SERVER STATE = START";

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        using (var cmd = new SqlCommand(createSql, conn))
            await cmd.ExecuteNonQueryAsync();

        using (var cmd = new SqlCommand(startSql, conn))
            await cmd.ExecuteNonQueryAsync();

        return sessionName;
    }

    /// <summary>
    /// Read trace events from the ring buffer. Non-destructive — can call repeatedly.
    /// </summary>
    public async Task<List<TraceEvent>> ReadEventsAsync(string connectionString, string sessionName)
    {
        var sql = $@"
SELECT CAST(target_data AS XML) AS trace_data
FROM sys.dm_xe_session_targets t
JOIN sys.dm_xe_sessions s ON t.event_session_address = s.address
WHERE s.name = '{sessionName.Replace("'", "''")}'
  AND t.target_name = 'ring_buffer'";

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        var result = await cmd.ExecuteScalarAsync();

        if (result == null || result == DBNull.Value)
            return [];

        return ParseRingBuffer(result.ToString()!);
    }

    /// <summary>
    /// Stop and drop the XE session.
    /// </summary>
    public async Task StopTraceAsync(string connectionString, string sessionName)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Stop first, then drop
            try
            {
                using var stopCmd = new SqlCommand(
                    $"ALTER EVENT SESSION [{sessionName}] ON SERVER STATE = STOP", conn);
                await stopCmd.ExecuteNonQueryAsync();
            }
            catch { /* may already be stopped */ }

            using var dropCmd = new SqlCommand(
                $"DROP EVENT SESSION [{sessionName}] ON SERVER", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TraceService.StopTrace", ex);
        }
    }

    /// <summary>
    /// Drop any orphaned LookoutTrace_* sessions left from crashes.
    /// </summary>
    public async Task CleanupOrphanedSessionsAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var findSql = "SELECT name FROM sys.server_event_sessions WHERE name LIKE 'LookoutTrace_%'";
            var sessions = new List<string>();

            using (var cmd = new SqlCommand(findSql, conn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    sessions.Add(reader.GetString(0));
            }

            foreach (var session in sessions)
            {
                try
                {
                    // Try stop (may not be running)
                    try
                    {
                        using var stopCmd = new SqlCommand(
                            $"ALTER EVENT SESSION [{session}] ON SERVER STATE = STOP", conn);
                        await stopCmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex) { AppLogger.LogError("Trace.StopOrphanedSession", ex); }

                    using var dropCmd = new SqlCommand(
                        $"DROP EVENT SESSION [{session}] ON SERVER", conn);
                    await dropCmd.ExecuteNonQueryAsync();

                    AppLogger.Log($"TraceService: cleaned up orphaned session {session}");
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"TraceService.Cleanup({session})", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TraceService.CleanupOrphaned", ex);
        }
    }

    // ── XE Event Definitions ─────────────────────────────────────────

    private static string BuildStatementEventDef(TraceOptions options)
    {
        var where = BuildWhereClause(options);
        return $@"
ADD EVENT sqlserver.sp_statement_completed(
    ACTION(
        sqlserver.database_name,
        sqlserver.session_id,
        sqlserver.username,
        sqlserver.client_hostname,
        sqlserver.client_app_name,
        sqlserver.sql_text
    )
    {where}
)";
    }

    private static string BuildBatchRpcEventDefs(TraceOptions options)
    {
        var where = BuildWhereClause(options);
        return $@"
ADD EVENT sqlserver.sql_batch_completed(
    ACTION(
        sqlserver.database_name,
        sqlserver.session_id,
        sqlserver.username,
        sqlserver.client_hostname,
        sqlserver.client_app_name,
        sqlserver.sql_text
    )
    {where}
),
ADD EVENT sqlserver.rpc_completed(
    ACTION(
        sqlserver.database_name,
        sqlserver.session_id,
        sqlserver.username,
        sqlserver.client_hostname,
        sqlserver.client_app_name,
        sqlserver.sql_text
    )
    {where}
)";
    }

    private static string BuildWhereClause(TraceOptions options)
    {
        var conditions = new List<string>();

        if (options.SpidFilter.HasValue)
            conditions.Add($"sqlserver.session_id = {options.SpidFilter.Value}");

        if (!string.IsNullOrEmpty(options.DatabaseFilter))
            conditions.Add($"sqlserver.database_name = N'{options.DatabaseFilter.Replace("'", "''")}'");

        if (options.MinDurationMs > 0)
            // XE durations are in microseconds
            conditions.Add($"duration >= {(long)options.MinDurationMs * 1000}");

        // Login and AppName filters applied client-side (XE WHERE doesn't support action predicates easily)

        if (conditions.Count == 0)
            return "";

        return "WHERE " + string.Join("\n      AND ", conditions);
    }

    // ── Ring Buffer XML Parsing ──────────────────────────────────────

    private static List<TraceEvent> ParseRingBuffer(string xml)
    {
        var events = new List<TraceEvent>();

        try
        {
            var doc = XDocument.Parse(xml);
            var eventElements = doc.Descendants("event");

            foreach (var evt in eventElements)
            {
                var traceEvent = new TraceEvent
                {
                    Timestamp = ParseTimestamp(evt.Attribute("timestamp")?.Value),
                    EventType = MapEventType(evt.Attribute("name")?.Value ?? ""),
                    DurationMs = GetDataValueLong(evt, "duration") / 1000, // microseconds → ms
                    CpuMs = GetDataValueLong(evt, "cpu_time") / 1000,
                    LogicalReads = GetDataValueLong(evt, "logical_reads"),
                    PhysicalReads = GetDataValueLong(evt, "physical_reads"),
                    Writes = GetDataValueLong(evt, "writes"),
                    RowCount = GetDataValueLong(evt, "row_count"),
                    SqlText = GetDataValueString(evt, "statement")
                             ?? GetDataValueString(evt, "batch_text")
                             ?? GetActionValueString(evt, "sql_text")
                             ?? "",
                    Database = GetActionValueString(evt, "database_name") ?? "",
                    SessionId = (int)GetActionValueLong(evt, "session_id"),
                    Login = GetActionValueString(evt, "username") ?? "",
                    Host = GetActionValueString(evt, "client_hostname") ?? "",
                    Application = GetActionValueString(evt, "client_app_name") ?? "",
                };

                events.Add(traceEvent);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("TraceService.ParseRingBuffer", ex);
        }

        return events.OrderBy(e => e.Timestamp).ToList();
    }

    private static DateTime ParseTimestamp(string? value)
    {
        if (value != null && DateTime.TryParse(value, out var dt))
            return dt;
        return DateTime.MinValue;
    }

    private static string MapEventType(string eventName) => eventName switch
    {
        "sp_statement_completed" => "statement",
        "sql_batch_completed" => "batch",
        "rpc_completed" => "rpc",
        _ => eventName
    };

    private static long GetDataValueLong(XElement evt, string name)
    {
        var element = evt.Elements("data")
            .FirstOrDefault(d => d.Attribute("name")?.Value == name);
        var val = element?.Element("value")?.Value;
        return val != null && long.TryParse(val, out var result) ? result : 0;
    }

    private static string? GetDataValueString(XElement evt, string name)
    {
        var element = evt.Elements("data")
            .FirstOrDefault(d => d.Attribute("name")?.Value == name);
        return element?.Element("value")?.Value;
    }

    private static long GetActionValueLong(XElement evt, string name)
    {
        var element = evt.Elements("action")
            .FirstOrDefault(a => a.Attribute("name")?.Value == name);
        var val = element?.Element("value")?.Value;
        return val != null && long.TryParse(val, out var result) ? result : 0;
    }

    private static string? GetActionValueString(XElement evt, string name)
    {
        var element = evt.Elements("action")
            .FirstOrDefault(a => a.Attribute("name")?.Value == name);
        return element?.Element("value")?.Value;
    }
}
