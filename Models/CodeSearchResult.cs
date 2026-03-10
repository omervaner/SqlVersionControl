namespace SqlVersionControl.Models;

public class CodeSearchResult
{
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string Definition { get; set; } = "";
    public int MatchCount { get; set; }
    public string Snippet { get; set; } = "";

    public string FullName => $"{SchemaName}.{ObjectName}";

    public string TypeIcon => ObjectType.ToUpperInvariant() switch
    {
        "SQL_STORED_PROCEDURE" => "⚙",
        "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION" => "ƒ",
        "VIEW" => "👁",
        "SQL_TRIGGER" => "⚡",
        _ => "○"
    };

    public string TypeShort => ObjectType.ToUpperInvariant() switch
    {
        "SQL_STORED_PROCEDURE" => "PROC",
        "SQL_SCALAR_FUNCTION" => "FN",
        "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION" => "TVF",
        "VIEW" => "VIEW",
        "SQL_TRIGGER" => "TR",
        _ => ObjectType
    };

    public string MatchCountDisplay => MatchCount == 1 ? "1 match" : $"{MatchCount} matches";
}
