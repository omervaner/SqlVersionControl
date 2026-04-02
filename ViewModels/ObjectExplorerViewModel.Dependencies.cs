using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVersionControl.Models;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel
{
    // ── Dependency Mode ────────────────────────────────────────────

    private List<ObjectExplorerNode>? _savedNodes;
    [ObservableProperty] private bool _isDependencyMode;

    public async Task ShowDependenciesAsync(ObjectExplorerNode node)
    {
        var connStr = ResolveConnectionString(node);
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;

        var (uses, usedBy) = connStr != null
            ? await _db.GetDependenciesAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetDependenciesAsync(node.DatabaseName, schema, node.Name);

        // Save current tree
        if (!IsDependencyMode)
            _savedNodes = RootNodes.ToList();

        IsDependencyMode = true;
        RootNodes.Clear();

        // Back button node
        RootNodes.Add(new ObjectExplorerNode
        {
            Name = "\u25c0 Back to Object Explorer",
            NodeType = ObjectExplorerNodeType.Folder,
            ConnectionId = node.ConnectionId
        });

        // Source object header
        RootNodes.Add(new ObjectExplorerNode
        {
            Name = $"Dependencies of {schema}.{node.Name}",
            NodeType = ObjectExplorerNodeType.Folder,
            IsCategoryFolder = true,
            ConnectionId = node.ConnectionId
        });

        // Uses section
        var usesFolder = new ObjectExplorerNode
        {
            Name = "Uses",
            NodeType = ObjectExplorerNodeType.Folder,
            IsCategoryFolder = true,
            ChildCount = uses.Count,
            ConnectionId = node.ConnectionId
        };
        foreach (var item in uses)
        {
            var depType = MapObjectType(item.ObjectType);
            usesFolder.Children.Add(new ObjectExplorerNode
            {
                Name = item.ObjectName,
                Schema = item.SchemaName,
                DatabaseName = node.DatabaseName,
                NodeType = depType,
                TypeInfo = item.ObjectType,
                ConnectionId = node.ConnectionId
            });
        }
        RootNodes.Add(usesFolder);

        // Used By section
        var usedByFolder = new ObjectExplorerNode
        {
            Name = "Used By",
            NodeType = ObjectExplorerNodeType.Folder,
            IsCategoryFolder = true,
            ChildCount = usedBy.Count,
            ConnectionId = node.ConnectionId
        };
        foreach (var item in usedBy)
        {
            var depType = MapObjectType(item.ObjectType);
            usedByFolder.Children.Add(new ObjectExplorerNode
            {
                Name = item.ObjectName,
                Schema = item.SchemaName,
                DatabaseName = node.DatabaseName,
                NodeType = depType,
                TypeInfo = item.ObjectType,
                ConnectionId = node.ConnectionId
            });
        }
        RootNodes.Add(usedByFolder);

        // Expand both sections
        usesFolder.IsExpanded = true;
        usedByFolder.IsExpanded = true;
    }

    public void BackFromDependencies()
    {
        if (_savedNodes == null) return;

        IsDependencyMode = false;
        RootNodes.Clear();
        foreach (var node in _savedNodes)
            RootNodes.Add(node);
        _savedNodes = null;
    }

    private static ObjectExplorerNodeType MapObjectType(string typeDesc) => typeDesc.ToUpperInvariant() switch
    {
        "SQL_STORED_PROCEDURE" => ObjectExplorerNodeType.Proc,
        "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION"
            => ObjectExplorerNodeType.Function,
        "VIEW" => ObjectExplorerNodeType.View,
        "SQL_TRIGGER" or "SQL_DML_TRIGGER" => ObjectExplorerNodeType.Trigger,
        "USER_TABLE" => ObjectExplorerNodeType.Table,
        _ => ObjectExplorerNodeType.Folder
    };
}
