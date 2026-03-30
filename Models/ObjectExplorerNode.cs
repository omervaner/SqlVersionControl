using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVersionControl.Models;

public enum ObjectExplorerNodeType
{
    Connection,
    Database,
    Folder,
    Table,
    View,
    Proc,
    Function,
    Column,
    Sequence,
    Job,
    Trigger,
    Parameter
}

public partial class ObjectExplorerNode : ObservableObject
{
    public string Name { get; set; } = "";
    public string Schema { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public ObjectExplorerNodeType NodeType { get; set; }
    [ObservableProperty] private string _typeInfo = ""; // e.g. "NVARCHAR(30)" for columns
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
    public string ParentTableName { get; set; } = ""; // For columns: the parent table name

    // Multi-connection support
    public string? ConnectionId { get; set; }       // Which registry connection this node belongs to
    public string? ConnectionColor { get; set; }    // Hex color for connection dot
    public string? Environment { get; set; }        // Dev/Test/Staging/Production

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _children = [];
    [ObservableProperty] private int _childCount;
    [ObservableProperty] private bool _isVisibleInFilter = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _rowCount = -1; // -1 = not loaded

    partial void OnChildCountChanged(int value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnRowCountChanged(long value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShowRowCount));
        OnPropertyChanged(nameof(FormattedRowCount));
    }
    partial void OnTypeInfoChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasTypeInfo));
    }

    /// <summary>Fired when a node is expanded and needs lazy loading.</summary>
    public event Action<ObjectExplorerNode>? ExpandRequested;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && HasDummyChild)
            ExpandRequested?.Invoke(this);
    }

    /// <summary>
    /// True when children contains only a dummy placeholder for the expand arrow.
    /// </summary>
    public bool HasDummyChild =>
        Children.Count == 1 && Children[0].NodeType == ObjectExplorerNodeType.Folder
        && Children[0].Name == "__dummy__";

    public string DisplayName => NodeType switch
    {
        ObjectExplorerNodeType.Connection => ConnectionDisplayName,
        ObjectExplorerNodeType.Column or ObjectExplorerNodeType.Parameter => Name,
        ObjectExplorerNodeType.Folder => ChildCount > 0 ? $"{Name} ({ChildCount})" : Name,
        ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
            or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
            or ObjectExplorerNodeType.Sequence
            => string.IsNullOrEmpty(Schema) || Schema == "dbo"
                ? Name
                : $"{Schema}.{Name}",
        ObjectExplorerNodeType.Job or ObjectExplorerNodeType.Trigger
            => string.IsNullOrEmpty(TypeInfo) ? Name : $"{Name} ({TypeInfo})",
        _ => Name
    };

    // ── Row count display (right-aligned in tree, separate from DisplayName) ──

    public bool ShowRowCount => NodeType == ObjectExplorerNodeType.Table && RowCount >= 0;

    public string FormattedRowCount => FormatRowCount(RowCount);

    private static string FormatRowCount(long count) => count switch
    {
        < 0 => "",
        < 1_000 => count.ToString(),
        < 1_000_000 => $"{count / 1_000.0:F1}K",
        < 1_000_000_000 => $"{count / 1_000_000.0:F1}M",
        _ => $"{count / 1_000_000_000.0:F1}B"
    };

    // ── Badge system (colored letter badges for OE nodes) ──

    public string BadgeLetter => NodeType switch
    {
        ObjectExplorerNodeType.Connection => "S",
        ObjectExplorerNodeType.Database => "", // uses cylinder icon instead
        ObjectExplorerNodeType.Table => "T",
        ObjectExplorerNodeType.View => "V",
        ObjectExplorerNodeType.Proc => "P",
        ObjectExplorerNodeType.Function => "\u0192", // ƒ
        ObjectExplorerNodeType.Sequence => "#",
        ObjectExplorerNodeType.Job => "J",
        ObjectExplorerNodeType.Trigger => "\u26a1", // ⚡
        ObjectExplorerNodeType.Column when IsPrimaryKey => "\u25cf", // ●
        ObjectExplorerNodeType.Column => "\u25cb", // ○
        ObjectExplorerNodeType.Parameter => "\u25c7", // ◇
        ObjectExplorerNodeType.Folder when IsCategoryFolder => FolderBadgeLetter,
        _ => ""
    };

    private string FolderBadgeLetter => Name switch
    {
        "Tables" => "T",
        "Views" => "V",
        "Stored Procedures" => "P",
        "Functions" => "\u0192", // ƒ
        "Sequences" => "#",
        "Types" => "\u03a9", // Ω
        "Database Triggers" => "\u26a1", // ⚡
        "Jobs" => "J",
        "Triggers" => "\u26a1", // ⚡
        _ => ""
    };

    /// <summary>Whether this node renders as a badge (rounded rect) vs inline character.</summary>
    public bool HasBadge => NodeType is ObjectExplorerNodeType.Connection
        or ObjectExplorerNodeType.Database
        or ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
        or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
        or ObjectExplorerNodeType.Sequence or ObjectExplorerNodeType.Job
        or ObjectExplorerNodeType.Trigger
        || (NodeType == ObjectExplorerNodeType.Folder && IsCategoryFolder && !string.IsNullOrEmpty(FolderBadgeLetter));

    /// <summary>Badge background opacity: folders bolder (0.15), objects subtler (0.08), connections solid (1.0).</summary>
    public double BadgeOpacity => NodeType switch
    {
        ObjectExplorerNodeType.Connection => 1.0,
        ObjectExplorerNodeType.Folder when IsCategoryFolder => 0.15,
        _ => 0.08
    };

    /// <summary>Badge letter foreground: white for connection nodes (solid bg), IconColor for others.</summary>
    public string BadgeLetterColor => NodeType == ObjectExplorerNodeType.Connection ? "#ffffff" : IconColor;

    /// <summary>Badge width.</summary>
    public double BadgeWidth => 18;

    // ── Connection node display split ──

    public string ConnectionDisplayName
    {
        get
        {
            if (NodeType != ObjectExplorerNodeType.Connection) return Name;
            var parenIdx = Name.LastIndexOf('(');
            return parenIdx > 0 ? Name[..parenIdx].Trim() : Name;
        }
    }

    public string ConnectionServerHint
    {
        get
        {
            if (NodeType != ObjectExplorerNodeType.Connection) return "";
            var parenIdx = Name.LastIndexOf('(');
            return parenIdx > 0 ? Name[parenIdx..] : "";
        }
    }

    public bool IsConnectionNode => NodeType == ObjectExplorerNodeType.Connection;
    public bool IsDatabaseNode => NodeType == ObjectExplorerNodeType.Database;

    // Computed helpers for template binding
    public bool IsColumn => NodeType is ObjectExplorerNodeType.Column or ObjectExplorerNodeType.Parameter;
    public bool HasTypeInfo => !string.IsNullOrEmpty(TypeInfo);
    public bool ShowPK => NodeType == ObjectExplorerNodeType.Column && IsPrimaryKey;
    public bool ShowNullability => NodeType == ObjectExplorerNodeType.Column;
    public bool IsBold => NodeType is ObjectExplorerNodeType.Connection or ObjectExplorerNodeType.Database or ObjectExplorerNodeType.Folder;
    /// <summary>True for top-level category folders (Tables, Views, etc.) — used for separator lines between groups.</summary>
    public bool IsCategoryFolder { get; set; }
    public double DisplayFontSize => NodeType is ObjectExplorerNodeType.Connection or ObjectExplorerNodeType.Database ? 13 : 12;
    public string NullabilityText => IsNullable ? "NULL" : "NOT NULL";
    public double DisplayFontWeight => NodeType is ObjectExplorerNodeType.Connection ? 700 : IsBold ? 600 : 400;

    public string IconColor => NodeType switch
    {
        ObjectExplorerNodeType.Connection => ConnectionColor ?? "#aaaaaa",
        ObjectExplorerNodeType.Database => "#888888",
        ObjectExplorerNodeType.Table => "#3d8b5e",
        ObjectExplorerNodeType.View => "#4a8fc0",
        ObjectExplorerNodeType.Proc => "#9a6aaf",
        ObjectExplorerNodeType.Function => "#cc8a3a",
        ObjectExplorerNodeType.Sequence => "#20a080",
        ObjectExplorerNodeType.Job => "#cc4f45",
        ObjectExplorerNodeType.Trigger => "#cc5f2e",
        ObjectExplorerNodeType.Column when IsPrimaryKey => "#f1c40f",
        ObjectExplorerNodeType.Column => "#888888",
        ObjectExplorerNodeType.Parameter => "#9b59b6",
        ObjectExplorerNodeType.Folder => FolderIconColor,
        _ => "#aaaaaa"
    };

    /// <summary>
    /// Folder color matches its children type — bolder shade for folder badges.
    /// </summary>
    private string FolderIconColor => Name switch
    {
        "Columns" => "#888888",
        "Tables" => "#4caf7a",
        "Views" => "#5ba3d9",
        "Stored Procedures" => "#b07cc7",
        "Functions" => "#e6a04e",
        "Sequences" => "#2dbf9a",
        "Jobs" => "#e8645a",
        "Triggers" => "#cc5f2e",
        "Parameters" => "#9b59b6",
        "Indexes" => "#5ba3d9",
        "Keys" => "#e8645a",
        "Constraints" => "#f39c12",
        "Types" => "#2dbf9a",
        "Database Triggers" => "#e8723a",
        _ => "#aaaaaa"
    };

    public static ObjectExplorerNode CreateDummy() => new()
    {
        Name = "__dummy__",
        NodeType = ObjectExplorerNodeType.Folder
    };
}
