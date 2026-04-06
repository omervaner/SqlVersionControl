namespace SqlVersionControl.Models;

public enum ExecutionOutcome { Success, PartialSuccess, Error }

public class ExecutionSummary
{
    public ExecutionOutcome Outcome { get; set; }
    public int ResultSetCount { get; set; }
    public int RowsReturned { get; set; }
    public int RowsAffected { get; set; }
    public int ErrorCount { get; set; }
    public string StatusText { get; set; } = "";
    public string FlashText { get; set; } = "";
}
