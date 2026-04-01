using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

public partial class DatabaseService
{
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
}
