using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _rootNodes = [];

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
                foreach (var (schema, name) in tables)
                {
                    folderNode.Children.Add(WireNode(new ObjectExplorerNode
                    {
                        Name = name,
                        Schema = schema,
                        DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Table,
                        Children = [ObjectExplorerNode.CreateDummy()]
                    }));
                }
                break;

            case "Views":
                var views = await _db.GetViewsAsync(db);
                foreach (var (schema, name) in views)
                {
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = name,
                        Schema = schema,
                        DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.View
                    });
                }
                break;

            case "Stored Procedures":
                var procsAndFuncs = await _db.GetProcsAndFunctionsAsync(db);
                foreach (var (schema, name, typeDesc) in procsAndFuncs
                    .Where(x => x.Type == "SQL_STORED_PROCEDURE"))
                {
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = name,
                        Schema = schema,
                        DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Proc
                    });
                }
                break;

            case "Functions":
                var funcs = await _db.GetProcsAndFunctionsAsync(db);
                foreach (var (schema, name, typeDesc) in funcs
                    .Where(x => x.Type != "SQL_STORED_PROCEDURE"))
                {
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = name,
                        Schema = schema,
                        DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Function
                    });
                }
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
