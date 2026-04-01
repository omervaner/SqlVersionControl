namespace SqlVersionControl.Models;

public enum MessageType { Info, RowCount, Error, Print, Timing }

public class QueryMessage
{
    public MessageType Type { get; set; }
    public string Text { get; set; } = "";
    public int LineNumber { get; set; } = -1; // for error click-to-navigate (-1 = no line)
}

public class QueryExecutionResult
{
    public List<QueryResult> Results { get; set; } = [];
    public List<QueryMessage> Messages { get; set; } = [];
    public int TotalRowsAffected { get; set; }
    public bool HasErrors { get; set; }
    public int ErrorCount { get; set; }
}
