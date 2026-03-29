namespace SqlVersionControl.Models;

public class QueryResult
{
    public string[] ColumnNames { get; set; } = [];
    public Type[] ColumnTypes { get; set; } = [];
    public List<object?[]> Rows { get; set; } = [];
    public int RowCount { get; set; }
    public long ExecutionTimeMs { get; set; }
    public string? Error { get; set; }
    public string? SourceSql { get; set; }
}
