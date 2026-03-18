namespace SqlVersionControl.Models;

/// <summary>
/// Column metadata from INFORMATION_SCHEMA + sys.default_constraints.
/// Used by TableCompareService for structural comparison between databases.
/// </summary>
public class TableColumnInfo
{
    public string Schema { get; set; } = "";
    public string TableName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int MaxLength { get; set; }
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public bool IsNullable { get; set; }
    public string? DefaultConstraintName { get; set; }
    public string? DefaultDefinition { get; set; }

    /// <summary>Full table key for grouping: [Schema].[TableName]</summary>
    public string TableKey => $"{Schema}.{TableName}";

    /// <summary>Formatted type string (e.g. "nvarchar(50)", "decimal(18,2)")</summary>
    public string FormattedType => Services.SqlTypeFormatter.Format(DataType, MaxLength, Precision, Scale);
}
