using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVersionControl.Models;

public enum TableCompareStatus
{
    Match,
    Different,
    SourceOnly,
    TargetOnly
}

public enum ColumnCompareStatus
{
    Match,
    Different,
    SourceOnly,
    TargetOnly
}

/// <summary>
/// Result of comparing one table between source and target databases.
/// </summary>
public class TableCompareResult
{
    public string Schema { get; set; } = "";
    public string TableName { get; set; } = "";
    public string TableKey => $"{Schema}.{TableName}";
    public TableCompareStatus Status { get; set; }
    public List<ColumnCompareResult> Columns { get; set; } = new();
}

/// <summary>
/// Result of comparing one column between source and target.
/// Observable so the DataGrid updates after per-column deploy.
/// </summary>
public partial class ColumnCompareResult : ObservableObject
{
    public string ColumnName { get; set; } = "";
    public TableColumnInfo? Source { get; set; }
    public TableColumnInfo? Target { get; set; }

    [ObservableProperty]
    private ColumnCompareStatus _status;

    // Display helpers for the comparison grid
    public string SourceType => Source?.FormattedType ?? "";
    public string TargetType => Target?.FormattedType ?? "";
    public string SourceNullable => Source != null ? (Source.IsNullable ? "NULL" : "NOT NULL") : "";
    public string TargetNullable => Target != null ? (Target.IsNullable ? "NULL" : "NOT NULL") : "";
    public string SourceDefault => Source?.DefaultDefinition ?? "";
    public string TargetDefault => Target?.DefaultDefinition ?? "";

    public string StatusText => Status switch
    {
        ColumnCompareStatus.Match => "Match",
        ColumnCompareStatus.Different => "Differ",
        ColumnCompareStatus.SourceOnly => "Source Only",
        ColumnCompareStatus.TargetOnly => "Target Only",
        _ => "?"
    };

    public bool IsDeployable => Status is ColumnCompareStatus.Different or ColumnCompareStatus.SourceOnly;

    partial void OnStatusChanged(ColumnCompareStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsDeployable));
    }
}
