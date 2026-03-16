using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private Timer? _filterDebounce;

    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _rootNodes = [];
    [ObservableProperty] private string _filterText = "";

    /// <summary>Fired when a context menu action wants to set editor text. Bool = auto-run.</summary>
    public event Action<string, bool>? InsertTextRequested;

    /// <summary>Fired when column double-click wants to insert text at cursor.</summary>
    public event Action<string>? InsertAtCursorRequested;

    /// <summary>Fired when "Edit Data" wants to run a SELECT and auto-enter edit mode.</summary>
    public event Action<string>? EditDataRequested;

    public ObjectExplorerViewModel(DatabaseService db)
    {
        _db = db;
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Dispose();
        _filterDebounce = new Timer(_ =>
            Dispatcher.UIThread.Post(ApplyFilter), null, 200, Timeout.Infinite);
    }

    public void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? "";
        foreach (var db in RootNodes)
            ApplyFilterToNode(db, filter);
    }

    private bool ApplyFilterToNode(ObjectExplorerNode node, string filter)
    {
        // No filter → everything visible
        if (string.IsNullOrEmpty(filter))
        {
            node.IsVisibleInFilter = true;
            foreach (var child in node.Children)
                ApplyFilterToNode(child, filter);
            return true;
        }

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Column:
                // Columns follow parent visibility — always visible
                node.IsVisibleInFilter = true;
                return true;

            case ObjectExplorerNodeType.Table:
            case ObjectExplorerNodeType.View:
            case ObjectExplorerNodeType.Proc:
            case ObjectExplorerNodeType.Function:
                // Leaf objects: match against name
                var matches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                node.IsVisibleInFilter = matches;
                // Still recurse children (columns) so they stay visible
                foreach (var child in node.Children)
                    child.IsVisibleInFilter = true;
                return matches;

            case ObjectExplorerNodeType.Database:
            case ObjectExplorerNodeType.Folder:
                // Containers: visible if any child matches
                var anyChildVisible = false;
                foreach (var child in node.Children)
                {
                    if (ApplyFilterToNode(child, filter))
                        anyChildVisible = true;
                }
                node.IsVisibleInFilter = anyChildVisible;
                return anyChildVisible;

            default:
                node.IsVisibleInFilter = true;
                return true;
        }
    }

    [RelayCommand]
    public void ClearFilter()
    {
        FilterText = "";
    }

    public async Task LoadDatabasesAsync(IEnumerable<string> databases)
    {
        RootNodes.Clear();

        foreach (var dbName in databases)
        {
            var node = WireNode(new ObjectExplorerNode
            {
                Name = dbName,
                DatabaseName = dbName,
                NodeType = ObjectExplorerNodeType.Database,
                Children = [ObjectExplorerNode.CreateDummy()]
            });
            RootNodes.Add(node);
        }
    }

    private ObjectExplorerNode WireNode(ObjectExplorerNode node)
    {
        node.ExpandRequested += n => _ = OnNodeExpandedAsync(n);
        return node;
    }

    public async Task OnNodeExpandedAsync(ObjectExplorerNode node)
    {
        if (!node.HasDummyChild) return;

        node.Children.Clear();
        node.IsLoading = true;

        try
        {
            switch (node.NodeType)
            {
                case ObjectExplorerNodeType.Database:
                    await LoadDatabaseChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Folder:
                    await LoadFolderChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Table:
                    await LoadColumnsAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            node.Children.Add(new ObjectExplorerNode
            {
                Name = $"Error: {ex.Message}",
                NodeType = ObjectExplorerNodeType.Folder
            });
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    /// <summary>
    /// Invalidates cached children for the node and re-triggers lazy loading from the server.
    /// </summary>
    public void RefreshNode(ObjectExplorerNode node)
    {
        // Clear all loaded children and reset to dummy — this invalidates the cache
        // so HasDummyChild returns true and the next expand fetches fresh data from DB.
        node.Children.Clear();
        node.ChildCount = 0;
        node.Children.Add(ObjectExplorerNode.CreateDummy());

        // Collapse then re-expand to trigger the lazy load
        node.IsExpanded = false;
        node.IsExpanded = true;
    }

    private Task LoadDatabaseChildrenAsync(ObjectExplorerNode dbNode)
    {
        var folders = new[]
        {
            ("Tables", ObjectExplorerNodeType.Folder),
            ("Views", ObjectExplorerNodeType.Folder),
            ("Stored Procedures", ObjectExplorerNodeType.Folder),
            ("Functions", ObjectExplorerNodeType.Folder),
        };

        foreach (var (name, type) in folders)
        {
            dbNode.Children.Add(WireNode(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = dbNode.DatabaseName,
                NodeType = type,
                Children = [ObjectExplorerNode.CreateDummy()]
            }));
        }

        return Task.CompletedTask;
    }

    private async Task LoadFolderChildrenAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;

        switch (folderNode.Name)
        {
            case "Tables":
                var tables = await _db.GetTablesAsync(db);
                var tableNodes = tables.Select(t => WireNode(new ObjectExplorerNode
                {
                    Name = t.Name, Schema = t.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Table,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }));
                await AddChildrenInBatchesAsync(folderNode, tableNodes);
                break;

            case "Views":
                var views = await _db.GetViewsAsync(db);
                var viewNodes = views.Select(v => new ObjectExplorerNode
                {
                    Name = v.Name, Schema = v.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.View
                });
                await AddChildrenInBatchesAsync(folderNode, viewNodes);
                break;

            case "Stored Procedures":
                var procsAndFuncs = await _db.GetProcsAndFunctionsAsync(db);
                var procNodes = procsAndFuncs
                    .Where(x => x.Type == "SQL_STORED_PROCEDURE")
                    .Select(p => new ObjectExplorerNode
                    {
                        Name = p.Name, Schema = p.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Proc
                    });
                await AddChildrenInBatchesAsync(folderNode, procNodes);
                break;

            case "Functions":
                var funcs = await _db.GetProcsAndFunctionsAsync(db);
                var funcNodes = funcs
                    .Where(x => x.Type != "SQL_STORED_PROCEDURE")
                    .Select(f => new ObjectExplorerNode
                    {
                        Name = f.Name, Schema = f.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Function
                    });
                await AddChildrenInBatchesAsync(folderNode, funcNodes);
                break;
        }

        if (folderNode.Children.Count == 0)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(empty)",
                NodeType = ObjectExplorerNodeType.Folder
            });
        }
        else
        {
            folderNode.ChildCount = folderNode.Children.Count;
        }

        // Re-apply filter if active so new children get filtered
        if (!string.IsNullOrEmpty(FilterText))
            ApplyFilter();
    }

    private async Task AddChildrenInBatchesAsync(
        ObjectExplorerNode parent,
        IEnumerable<ObjectExplorerNode> children,
        int batchSize = 50)
    {
        var batch = new List<ObjectExplorerNode>();
        foreach (var child in children)
        {
            batch.Add(child);
            if (batch.Count >= batchSize)
            {
                var toAdd = batch.ToList();
                batch.Clear();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var node in toAdd)
                        parent.Children.Add(node);
                });
            }
        }
        if (batch.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var node in batch)
                    parent.Children.Add(node);
            });
        }
    }

    private async Task LoadColumnsAsync(ObjectExplorerNode tableNode)
    {
        var columns = await _db.GetColumnsAsync(
            tableNode.DatabaseName, tableNode.Schema, tableNode.Name);

        foreach (var (name, typeName, maxLength, isNullable, isPk) in columns)
        {
            var typeInfo = FormatColumnType(typeName, maxLength);

            tableNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = tableNode.DatabaseName,
                Schema = tableNode.Schema,
                NodeType = ObjectExplorerNodeType.Column,
                TypeInfo = typeInfo,
                IsPrimaryKey = isPk,
                IsNullable = isNullable,
                ParentTableName = tableNode.Name
            });
        }
    }

    private static string FormatColumnType(string typeName, int maxLength)
    {
        var upper = typeName.ToUpperInvariant();

        // Types that use max_length
        if (upper is "NVARCHAR" or "NCHAR")
            return maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength / 2})";
        if (upper is "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY")
            return maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength})";

        // Types that don't need length
        return typeName;
    }

    // ── Context Menu Actions ────────────────────────────────────────

    public void SelectTop100(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 100 * FROM [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void SelectCount(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT COUNT(*) FROM [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public async Task ViewDefinitionAsync(ObjectExplorerNode node)
    {
        var definition = await _db.GetObjectDefinitionAsync(
            node.DatabaseName, string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema, node.Name);
        if (definition != null)
            InsertTextRequested?.Invoke(definition, false);
    }

    public void ScriptAsCreate(ObjectExplorerNode node)
    {
        // Same as ViewDefinition — the definition IS the CREATE script
        _ = ViewDefinitionAsync(node);
    }

    public async Task GenerateExecAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var parameters = await _db.GetProcParametersAsync(node.DatabaseName, schema, node.Name);

        var sql = $"EXEC [{schema}].[{node.Name}]";
        if (parameters.Count > 0)
        {
            var paramList = string.Join(",\n     ",
                parameters.Select(p => $"{p.Name} = NULL /* {p.TypeName} */"));
            sql += "\n     " + paramList;
        }

        InsertTextRequested?.Invoke(sql, false);
    }

    public void SelectDistinct(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT DISTINCT [{node.Name}] FROM [{schema}].[{table}] ORDER BY [{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void InsertColumnName(ObjectExplorerNode node)
    {
        InsertAtCursorRequested?.Invoke($"[{node.Name}]");
    }

    public void EditData(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 200 * FROM [{schema}].[{node.Name}]";
        EditDataRequested?.Invoke(sql);
    }
}
