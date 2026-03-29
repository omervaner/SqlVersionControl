using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVersionControl.Models;

public enum DataRowStatus
{
    Identical,
    Different,
    SourceOnly,
    TargetOnly
}

/// <summary>
/// Result of comparing row data between source and target tables.
/// </summary>
public class DataCompareResult
{
    public string Schema { get; set; } = "";
    public string TableName { get; set; } = "";
    public string[] ColumnNames { get; set; } = [];
    public string[] PkColumnNames { get; set; } = [];
    public int[] PkColumnIndices { get; set; } = [];
    public bool HasIdentityColumn { get; set; }
    public List<DataCompareRow> Rows { get; set; } = [];

    public int IdenticalCount => Rows.Count(r => r.Status == DataRowStatus.Identical);
    public int DifferentCount => Rows.Count(r => r.Status == DataRowStatus.Different);
    public int SourceOnlyCount => Rows.Count(r => r.Status == DataRowStatus.SourceOnly);
    public int TargetOnlyCount => Rows.Count(r => r.Status == DataRowStatus.TargetOnly);

    public string Summary =>
        $"{Rows.Count} rows compared: {IdenticalCount} identical, {DifferentCount} different, {SourceOnlyCount} source only, {TargetOnlyCount} target only";
}

/// <summary>
/// One row matched by primary key between source and target.
/// </summary>
public partial class DataCompareRow : ObservableObject
{
    public DataRowStatus Status { get; set; }
    public object?[] SourceValues { get; set; } = [];
    public object?[] TargetValues { get; set; } = [];
    public HashSet<int> DifferentColumnIndices { get; set; } = [];

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Status icon for the master grid.</summary>
    public string StatusIcon => Status switch
    {
        DataRowStatus.Identical => "\u2713",   // ✓
        DataRowStatus.Different => "\u2260",   // ≠
        DataRowStatus.SourceOnly => "\u2190",  // ←
        DataRowStatus.TargetOnly => "\u2192",  // →
        _ => "?"
    };

    public string StatusColor => Status switch
    {
        DataRowStatus.Identical => "#27ae60",
        DataRowStatus.Different => "#e67e22",
        DataRowStatus.SourceOnly => "#27ae60",
        DataRowStatus.TargetOnly => "#e74c3c",
        _ => "#95a5a6"
    };

    /// <summary>Get the display value for a column (shows source value, or target if source-only).</summary>
    public object? GetDisplayValue(int columnIndex) => Status == DataRowStatus.TargetOnly
        ? TargetValues.ElementAtOrDefault(columnIndex)
        : SourceValues.ElementAtOrDefault(columnIndex);

    /// <summary>Get the PK display string for this row.</summary>
    public string GetPkDisplay(int[] pkIndices)
    {
        var values = Status == DataRowStatus.TargetOnly ? TargetValues : SourceValues;
        return string.Join(", ", pkIndices.Select(i => values.ElementAtOrDefault(i)?.ToString() ?? "NULL"));
    }
}

/// <summary>
/// Field-level detail for the detail panel.
/// </summary>
public class DataCompareField
{
    public string ColumnName { get; set; } = "";
    public string SourceDisplay { get; set; } = "";
    public string TargetDisplay { get; set; } = "";
    public bool IsDifferent { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsSourceNull { get; set; }
    public bool IsTargetNull { get; set; }

    public string StatusText => IsDifferent ? "Different" : "Match";
}
