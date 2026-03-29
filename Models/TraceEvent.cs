namespace SqlVersionControl.Models;

public class TraceEvent
{
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = "";     // "statement", "batch", "rpc"
    public string Database { get; set; } = "";
    public string Login { get; set; } = "";
    public string Host { get; set; } = "";
    public string Application { get; set; } = "";
    public int SessionId { get; set; }
    public long DurationMs { get; set; }
    public long CpuMs { get; set; }
    public long LogicalReads { get; set; }
    public long PhysicalReads { get; set; }
    public long Writes { get; set; }
    public long RowCount { get; set; }
    public string SqlText { get; set; } = "";

    public string SqlTextPreview => SqlText.Length > 200
        ? SqlText[..200] + "..."
        : SqlText;

    public string DurationDisplay => DurationMs >= 1000
        ? $"{DurationMs / 1000.0:F1}s"
        : $"{DurationMs}ms";
}

public class TraceOptions
{
    public int? SpidFilter { get; set; }
    public string? DatabaseFilter { get; set; }
    public string? LoginFilter { get; set; }
    public string? AppNameFilter { get; set; }
    public int MinDurationMs { get; set; }
    public string? TextContains { get; set; }

    /// <summary>
    /// True = capture sp_statement_completed (internal statements within a proc, Mode 1).
    /// False = capture sql_batch_completed + rpc_completed (completed queries, Mode 2 &amp; 3).
    /// </summary>
    public bool CaptureStatements { get; set; }
}
