using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
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
}
