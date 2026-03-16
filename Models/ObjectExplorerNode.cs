using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVersionControl.Models;

public enum ObjectExplorerNodeType
{
    Database,
    Folder,
    Table,
    View,
    Proc,
    Function,
    Column
}

public partial class ObjectExplorerNode : ObservableObject
{
    public string Name { get; set; } = "";
    public string Schema { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public ObjectExplorerNodeType NodeType { get; set; }
    public string TypeInfo { get; set; } = ""; // e.g. "NVARCHAR(30)" for columns
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
    public string ParentTableName { get; set; } = ""; // For columns: the parent table name

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _children = [];
    [ObservableProperty] private int _childCount;
    [ObservableProperty] private bool _isVisibleInFilter = true;
    [ObservableProperty] private bool _isLoading;

    partial void OnChildCountChanged(int value) => OnPropertyChanged(nameof(DisplayName));

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
        ObjectExplorerNodeType.Column => Name,
        ObjectExplorerNodeType.Folder => ChildCount > 0 ? $"{Name} ({ChildCount})" : Name,
        ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
            or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
            => string.IsNullOrEmpty(Schema) || Schema == "dbo"
                ? Name
                : $"{Schema}.{Name}",
        _ => Name
    };

    // Computed helpers for template binding
    public bool IsColumn => NodeType == ObjectExplorerNodeType.Column;
    public bool ShowPK => IsColumn && IsPrimaryKey;
    public bool IsBold => NodeType is ObjectExplorerNodeType.Database or ObjectExplorerNodeType.Folder;
    public double DisplayFontSize => NodeType == ObjectExplorerNodeType.Database ? 13 : 12;
    public string NullabilityText => IsNullable ? "NULL" : "NOT NULL";

    public string Icon => NodeType switch
    {
        ObjectExplorerNodeType.Column when IsPrimaryKey => "\u25cf", // ●
        ObjectExplorerNodeType.Column => "\u25cb",                   // ○
        _ => "\u25cf"                                                // ●
    };

    public string IconColor => NodeType switch
    {
        ObjectExplorerNodeType.Database => "#aaaaaa",
        ObjectExplorerNodeType.Table => "#2a6e4e",
        ObjectExplorerNodeType.View => "#2980b9",
        ObjectExplorerNodeType.Proc => "#8e44ad",
        ObjectExplorerNodeType.Function => "#e67e22",
        ObjectExplorerNodeType.Column when IsPrimaryKey => "#f1c40f",
        ObjectExplorerNodeType.Column => "#888888",
        ObjectExplorerNodeType.Folder => FolderIconColor,
        _ => "#aaaaaa"
    };

    /// <summary>
    /// Folder color matches its children type.
    /// </summary>
    private string FolderIconColor => Name switch
    {
        "Tables" => "#2a6e4e",
        "Views" => "#2980b9",
        "Stored Procedures" => "#8e44ad",
        "Functions" => "#e67e22",
        _ => "#aaaaaa"
    };

    public static ObjectExplorerNode CreateDummy() => new()
    {
        Name = "__dummy__",
        NodeType = ObjectExplorerNodeType.Folder
    };
}
