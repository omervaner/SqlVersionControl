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
    private ConnectionRegistry? _registry;
    private Timer? _filterDebounce;

    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _rootNodes = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showFilterEmptyState;

    /// <summary>Fired when a context menu action wants to set editor text. Bool = auto-run.</summary>
    public event Action<string, bool>? InsertTextRequested;

    /// <summary>Fired when column double-click wants to insert text at cursor.</summary>
    public event Action<string>? InsertAtCursorRequested;

    /// <summary>Fired when "Edit Data" wants to run a SELECT and auto-enter edit mode.</summary>
    public event Action<string>? EditDataRequested;

    /// <summary>Fired when a sequence context menu wants to show the Alter dialog.</summary>
    public event Action<ObjectExplorerNode>? AlterSequenceRequested;

    /// <summary>Fired when "Reset to 0" wants confirmation and execution.</summary>
    public event Action<ObjectExplorerNode>? ResetSequenceRequested;

    /// <summary>Fired when "Start Job" wants confirmation and execution.</summary>
    public event Action<ObjectExplorerNode>? StartJobRequested;

    /// <summary>Fired when dependency mode wants to peek a definition in the results panel.</summary>
    public event Action<string, string>? PeekDefinitionRequested; // (objectName, definition)

    public void RequestAlterSequence(ObjectExplorerNode node) => AlterSequenceRequested?.Invoke(node);
    public void RequestResetSequence(ObjectExplorerNode node) => ResetSequenceRequested?.Invoke(node);
    public void RequestStartJob(ObjectExplorerNode node) => StartJobRequested?.Invoke(node);

    // Legacy: per-tab active connection (used when registry is not available)
    private string? _activeConnectionString;

    public void SetActiveConnection(string? connectionString)
    {
        _activeConnectionString = connectionString;
    }

    public void SetRegistry(ConnectionRegistry registry)
    {
        _registry = registry;

        // Subscribe to connection state changes
        registry.ConnectionStateChanged += OnConnectionStateChanged;
        registry.ConnectionAdded += OnConnectionAdded;
        registry.ConnectionRemoved += OnConnectionRemoved;
    }

    public ObjectExplorerViewModel(DatabaseService db)
    {
        _db = db;
    }

    // ── Multi-connection: registry-driven root nodes ─────────────────

    /// <summary>
    /// Populate OE root with connection nodes from the registry.
    /// Each active connection becomes a root node that expands to show databases.
    /// </summary>
    public void LoadFromRegistry()
    {
        if (_registry == null) return;

        RootNodes.Clear();
        foreach (var managed in _registry.Connections.Where(c => c.IsConnected))
        {
            var node = CreateConnectionNode(managed);
            RootNodes.Add(node);
        }
    }

    private ObjectExplorerNode CreateConnectionNode(ManagedConnection managed)
    {
        var displayName = managed.Config.Name ?? managed.Config.Server;
        var serverSuffix = managed.Config.Name != null ? $" ({managed.Config.Server})" : "";

        var node = WireNode(new ObjectExplorerNode
        {
            Name = $"{displayName}{serverSuffix}",
            NodeType = ObjectExplorerNodeType.Connection,
            ConnectionId = managed.Id,
            ConnectionColor = managed.Color ?? "#88a1bb",
            Environment = managed.Environment,
            Children = managed.IsConnected
                ? [ObjectExplorerNode.CreateDummy()]
                : []
        });

        if (!managed.IsConnected)
        {
            // Placeholder child for "Click to connect"
            node.Children.Add(new ObjectExplorerNode
            {
                Name = "Click to connect",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = managed.Id,
            });
        }

        return node;
    }

    private void OnConnectionStateChanged(ManagedConnection managed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var existing = RootNodes.FirstOrDefault(n => n.ConnectionId == managed.Id);
            if (existing != null)
            {
                var idx = RootNodes.IndexOf(existing);
                var newNode = CreateConnectionNode(managed);

                // Preserve expanded state if reconnecting
                if (managed.IsConnected && existing.Children.Count > 0 &&
                    existing.Children[0].Name != "Click to connect")
                {
                    // Keep the existing expanded tree
                    return;
                }

                RootNodes[idx] = newNode;
            }
            else if (managed.IsConnected)
            {
                // Node was removed (e.g. by disconnect context menu) — re-add it
                RootNodes.Add(CreateConnectionNode(managed));
            }
        });
    }

    private void OnConnectionAdded(ManagedConnection managed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (RootNodes.Any(n => n.ConnectionId == managed.Id)) return;
            RootNodes.Add(CreateConnectionNode(managed));
        });
    }

    private void OnConnectionRemoved(ManagedConnection managed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var node = RootNodes.FirstOrDefault(n => n.ConnectionId == managed.Id);
            if (node != null)
                RootNodes.Remove(node);
        });
    }

    // ── Connection string resolution ─────────────────────────────────

    /// <summary>
    /// Resolve a connection string for a node. Uses ConnectionId (registry) if available,
    /// falls back to _activeConnectionString (legacy per-tab mode).
    /// </summary>
    private string? ResolveConnectionString(ObjectExplorerNode node)
    {
        if (node.ConnectionId != null && _registry != null)
            return _registry.GetConnectionString(node.ConnectionId);
        return _activeConnectionString;
    }

    // ── Filter ───────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Dispose();
        _filterDebounce = new Timer(_ =>
            Dispatcher.UIThread.Post(ApplyFilter), null, 200, Timeout.Infinite);
    }

    public void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? "";
        var anyVisible = false;
        foreach (var db in RootNodes)
        {
            if (ApplyFilterToNode(db, filter))
                anyVisible = true;
        }
        ShowFilterEmptyState = !string.IsNullOrEmpty(filter) && !anyVisible;
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
            case ObjectExplorerNodeType.Parameter:
                // Columns/Parameters follow parent visibility — always visible
                node.IsVisibleInFilter = true;
                return true;

            case ObjectExplorerNodeType.Table:
            case ObjectExplorerNodeType.View:
            case ObjectExplorerNodeType.Proc:
            case ObjectExplorerNodeType.Function:
            case ObjectExplorerNodeType.Sequence:
            case ObjectExplorerNodeType.Job:
            case ObjectExplorerNodeType.Trigger:
                // Leaf objects: match against name
                var matches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                node.IsVisibleInFilter = matches;
                // Still recurse children (columns) so they stay visible
                foreach (var child in node.Children)
                    child.IsVisibleInFilter = true;
                return matches;

            case ObjectExplorerNodeType.Connection:
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

    // ── Legacy: single-connection database loading ───────────────────

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

    // ── Node wiring & expand ─────────────────────────────────────────

    private ObjectExplorerNode WireNode(ObjectExplorerNode node)
    {
        node.ExpandRequested += n => _ = OnNodeExpandedAsync(n);
        return node;
    }

    /// <summary>
    /// Propagate ConnectionId to a child node from its parent.
    /// </summary>
    private ObjectExplorerNode WireChild(ObjectExplorerNode child, ObjectExplorerNode parent)
    {
        child.ConnectionId = parent.ConnectionId;
        return WireNode(child);
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
                case ObjectExplorerNodeType.Connection:
                    await LoadConnectionChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Database:
                    await LoadDatabaseChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Folder:
                    await LoadFolderChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Table:
                    await LoadTableChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.View:
                    await LoadViewChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Proc:
                case ObjectExplorerNodeType.Function:
                    await LoadProcChildrenAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            node.Children.Add(new ObjectExplorerNode
            {
                Name = $"Error: {ex.Message}",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = node.ConnectionId
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

    // ── Connection node expand → databases ───────────────────────────

    private async Task LoadConnectionChildrenAsync(ObjectExplorerNode connNode)
    {
        var connStr = ResolveConnectionString(connNode);
        if (connStr == null)
        {
            connNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(not connected)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = connNode.ConnectionId
            });
            return;
        }

        var databases = await _db.GetDatabasesAsync(connStr);
        foreach (var dbName in databases)
        {
            connNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = dbName,
                DatabaseName = dbName,
                NodeType = ObjectExplorerNodeType.Database,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, connNode));
        }

        if (connNode.Children.Count == 0)
        {
            connNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(no databases)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = connNode.ConnectionId
            });
        }
    }

    // ── Database node expand → folders ───────────────────────────────

    private Task LoadDatabaseChildrenAsync(ObjectExplorerNode dbNode)
    {
        var folders = new[]
        {
            ("Tables", ObjectExplorerNodeType.Folder),
            ("Views", ObjectExplorerNodeType.Folder),
            ("Stored Procedures", ObjectExplorerNodeType.Folder),
            ("Functions", ObjectExplorerNodeType.Folder),
            ("Sequences", ObjectExplorerNodeType.Folder),
            ("Types", ObjectExplorerNodeType.Folder),
            ("Database Triggers", ObjectExplorerNodeType.Folder),
            ("Jobs", ObjectExplorerNodeType.Folder),
        };

        foreach (var (name, type) in folders)
        {
            dbNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = dbNode.DatabaseName,
                NodeType = type,
                IsCategoryFolder = true,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, dbNode));
        }

        return Task.CompletedTask;
    }

    // ── Folder node expand → objects ─────────────────────────────────

    private async Task LoadFolderChildrenAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        switch (folderNode.Name)
        {
            case "Tables":
                var tables = connStr != null
                    ? await _db.GetTablesAsync(connStr, db)
                    : await _db.GetTablesAsync(db);
                var tableNodes = tables.Select(t => WireChild(new ObjectExplorerNode
                {
                    Name = t.Name, Schema = t.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Table,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, tableNodes);
                break;

            case "Views":
                var views = connStr != null
                    ? await _db.GetViewsAsync(connStr, db)
                    : await _db.GetViewsAsync(db);
                var viewNodes = views.Select(v => WireChild(new ObjectExplorerNode
                {
                    Name = v.Name, Schema = v.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.View,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, viewNodes);
                break;

            case "Stored Procedures":
                var procsAndFuncs = connStr != null
                    ? await _db.GetProcsAndFunctionsAsync(connStr, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var procNodes = procsAndFuncs
                    .Where(x => x.Type == "SQL_STORED_PROCEDURE")
                    .Select(p => WireChild(new ObjectExplorerNode
                    {
                        Name = p.Name, Schema = p.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Proc,
                        Children = [ObjectExplorerNode.CreateDummy()]
                    }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, procNodes);
                break;

            case "Functions":
                var funcs = connStr != null
                    ? await _db.GetProcsAndFunctionsAsync(connStr, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var funcNodes = funcs
                    .Where(x => x.Type != "SQL_STORED_PROCEDURE")
                    .Select(f => WireChild(new ObjectExplorerNode
                    {
                        Name = f.Name, Schema = f.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Function,
                        Children = [ObjectExplorerNode.CreateDummy()]
                    }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, funcNodes);
                break;

            case "Sequences":
                var sequences = connStr != null
                    ? await _db.GetSequencesAsync(connStr, db)
                    : await _db.GetSequencesAsync(db);
                var seqNodes = sequences.Select(seq => new ObjectExplorerNode
                {
                    Name = seq.Name, Schema = seq.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Sequence,
                    TypeInfo = $"{seq.DataType}, Current: {seq.CurrentValue}",
                    ConnectionId = folderNode.ConnectionId
                });
                await AddChildrenInBatchesAsync(folderNode, seqNodes);
                break;

            case "Columns":
                await LoadColumnsAsync(folderNode);
                break;

            case "Parameters":
                await LoadParametersAsync(folderNode);
                break;

            case "Indexes":
                await LoadIndexesAsync(folderNode);
                break;

            case "Keys":
                await LoadForeignKeysAsync(folderNode);
                break;

            case "Constraints":
                await LoadConstraintsAsync(folderNode);
                break;

            case "Triggers":
                await LoadTableTriggersAsync(folderNode);
                break;

            case "Types":
                await LoadUserTypesAsync(folderNode);
                break;

            case "Database Triggers":
                await LoadDatabaseTriggersAsync(folderNode);
                break;

            case "Jobs":
                try
                {
                    var jobs = connStr != null
                        ? await _db.GetJobsAsync(connStr)
                        : await _db.GetJobsAsync();
                    var jobNodes = jobs.Select(j => new ObjectExplorerNode
                    {
                        Name = j.Name, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Job,
                        TypeInfo = j.Enabled
                            ? $"Enabled, Last: {j.LastOutcome}"
                            : "Disabled",
                        ConnectionId = folderNode.ConnectionId
                    });
                    await AddChildrenInBatchesAsync(folderNode, jobNodes);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("ObjectExplorer.LoadJobs", ex);
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = $"(Error: {ex.Message})",
                        NodeType = ObjectExplorerNodeType.Folder,
                        ConnectionId = folderNode.ConnectionId
                    });
                }
                break;
        }

        if (folderNode.Children.Count == 0)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(empty)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = folderNode.ConnectionId
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

    // ── Table expand → Columns + Triggers folders ────────────────────

    private Task LoadTableChildrenAsync(ObjectExplorerNode tableNode)
    {
        var subfolders = new[] { "Columns", "Indexes", "Keys", "Constraints", "Triggers" };
        foreach (var name in subfolders)
        {
            tableNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = tableNode.DatabaseName,
                Schema = tableNode.Schema,
                ParentTableName = tableNode.Name,
                NodeType = ObjectExplorerNodeType.Folder,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, tableNode));
        }

        return Task.CompletedTask;
    }

    // ── View expand → Columns folder ───────────────────────────────

    private Task LoadViewChildrenAsync(ObjectExplorerNode viewNode)
    {
        var columnsFolder = WireChild(new ObjectExplorerNode
        {
            Name = "Columns",
            DatabaseName = viewNode.DatabaseName,
            Schema = viewNode.Schema,
            ParentTableName = viewNode.Name,
            NodeType = ObjectExplorerNodeType.Folder,
            Children = [ObjectExplorerNode.CreateDummy()]
        }, viewNode);
        viewNode.Children.Add(columnsFolder);

        return Task.CompletedTask;
    }

    // ── Proc/Function expand → Parameters folder ────────────────────

    private Task LoadProcChildrenAsync(ObjectExplorerNode procNode)
    {
        var paramsFolder = WireChild(new ObjectExplorerNode
        {
            Name = "Parameters",
            DatabaseName = procNode.DatabaseName,
            Schema = procNode.Schema,
            ParentTableName = procNode.Name,
            NodeType = ObjectExplorerNodeType.Folder,
            Children = [ObjectExplorerNode.CreateDummy()]
        }, procNode);
        procNode.Children.Add(paramsFolder);

        return Task.CompletedTask;
    }

    private async Task LoadParametersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var procName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var parameters = connStr != null
            ? await _db.GetProcParametersDetailedAsync(connStr, db, schema, procName)
            : await _db.GetProcParametersDetailedAsync(db, schema, procName);

        foreach (var (name, typeName, maxLength, precision, scale, isOutput) in parameters)
        {
            var typeInfo = SqlTypeFormatter.Format(typeName, maxLength, precision, scale);
            if (isOutput) typeInfo += " OUTPUT";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Parameter,
                TypeInfo = typeInfo,
                ParentTableName = procName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadIndexesAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var indexes = connStr != null
            ? await _db.GetTableIndexesAsync(connStr, db, schema, tableName)
            : await _db.GetTableIndexesAsync(db, schema, tableName);

        foreach (var (name, typeDesc, keyCols, includeCols) in indexes)
        {
            var typeInfo = typeDesc;
            if (!string.IsNullOrEmpty(keyCols))
                typeInfo += $" ({keyCols})";
            if (!string.IsNullOrEmpty(includeCols))
                typeInfo += $" INCLUDE ({includeCols})";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadForeignKeysAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var fks = connStr != null
            ? await _db.GetForeignKeysAsync(connStr, db, schema, tableName)
            : await _db.GetForeignKeysAsync(db, schema, tableName);

        foreach (var (name, refTable, colMapping, deleteAction, updateAction) in fks)
        {
            var typeInfo = $"→ {refTable}";
            if (!string.IsNullOrEmpty(colMapping))
                typeInfo += $" ({colMapping})";
            if (deleteAction != "NO_ACTION")
                typeInfo += $" ON DELETE {deleteAction}";
            if (updateAction != "NO_ACTION")
                typeInfo += $" ON UPDATE {updateAction}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadConstraintsAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var constraints = connStr != null
            ? await _db.GetConstraintsAsync(connStr, db, schema, tableName)
            : await _db.GetConstraintsAsync(db, schema, tableName);

        foreach (var (name, constraintType, expression, columnName) in constraints)
        {
            var typeInfo = constraintType == "DEFAULT"
                ? $"DEFAULT on [{columnName}]: {expression}"
                : $"CHECK: {expression}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadUserTypesAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        var types = connStr != null
            ? await _db.GetUserTypesAsync(connStr, db)
            : await _db.GetUserTypesAsync(db);

        foreach (var (name, baseType, isTableType) in types)
        {
            var node = new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = baseType,
                ConnectionId = folderNode.ConnectionId
            };

            folderNode.Children.Add(node);
        }
    }

    private async Task LoadDatabaseTriggersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        var triggers = connStr != null
            ? await _db.GetDatabaseTriggersAsync(connStr, db)
            : await _db.GetDatabaseTriggersAsync(db);

        foreach (var (name, isEnabled, eventTypes) in triggers)
        {
            var typeInfo = isEnabled ? "" : "Disabled";
            if (!string.IsNullOrEmpty(eventTypes))
                typeInfo = string.IsNullOrEmpty(typeInfo) ? eventTypes : $"{typeInfo}, {eventTypes}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                NodeType = ObjectExplorerNodeType.Trigger,
                TypeInfo = typeInfo,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadColumnsAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, db, schema, tableName)
            : await _db.GetColumnsAsync(db, schema, tableName);

        foreach (var (name, typeName, maxLength, isNullable, isPk) in columns)
        {
            var typeInfo = FormatColumnType(typeName, maxLength);

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Column,
                TypeInfo = typeInfo,
                IsPrimaryKey = isPk,
                IsNullable = isNullable,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadTableTriggersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var triggers = connStr != null
            ? await _db.GetTriggersAsync(connStr, db)
            : await _db.GetTriggersAsync(db);

        var tableTrigs = triggers.Where(t =>
            t.ParentTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase));

        foreach (var t in tableTrigs)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = t.Name, Schema = t.Schema, DatabaseName = db,
                ParentTableName = t.ParentTable,
                NodeType = ObjectExplorerNodeType.Trigger,
                TypeInfo = t.IsEnabled ? "" : "Disabled",
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private static string FormatColumnType(string typeName, int maxLength)
        => SqlTypeFormatter.Format(typeName, maxLength);

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
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var definition = connStr != null
            ? await _db.GetObjectDefinitionAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition != null)
            InsertTextRequested?.Invoke(definition, false);
    }

    public void ScriptAsCreate(ObjectExplorerNode node)
    {
        // Same as ViewDefinition — the definition IS the CREATE script
        _ = ViewDefinitionAsync(node);
    }

    /// <summary>Script as ALTER — fetch definition and replace CREATE with ALTER.</summary>
    public async Task ScriptAsAlterAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var definition = connStr != null
            ? await _db.GetObjectDefinitionAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition == null) return;

        // Replace CREATE with ALTER (skip if already ALTER)
        var altered = System.Text.RegularExpressions.Regex.Replace(
            definition,
            @"\bCREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b",
            "ALTER $2",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        InsertTextRequested?.Invoke(altered, false);
    }

    /// <summary>Script as DROP with IF EXISTS safety.</summary>
    public void ScriptAsDrop(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var objectType = node.NodeType switch
        {
            ObjectExplorerNodeType.Table => "TABLE",
            ObjectExplorerNodeType.View => "VIEW",
            ObjectExplorerNodeType.Proc => "PROCEDURE",
            ObjectExplorerNodeType.Function => "FUNCTION",
            ObjectExplorerNodeType.Trigger => "TRIGGER",
            _ => "OBJECT"
        };
        var sql = $"DROP {objectType} IF EXISTS [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, false);
    }

    public void ToggleTrigger(ObjectExplorerNode node, bool enable)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var parentTable = node.ParentTableName;
        var action = enable ? "ENABLE" : "DISABLE";
        var sql = $"{action} TRIGGER [{schema}].[{node.Name}] ON [{schema}].[{parentTable}]";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate INSERT template with all columns and placeholder values.</summary>
    public async Task ScriptAsInsertAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var colNames = string.Join(",\n    ", columns.Select(c => $"[{c.Name}]"));
        var colValues = string.Join(",\n    ", columns.Select(c => $"/* {c.TypeName} */ NULL"));
        var sql = $"INSERT INTO [{schema}].[{node.Name}]\n(\n    {colNames}\n)\nVALUES\n(\n    {colValues}\n)";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate ALTER TABLE ADD column template.</summary>
    public void ScriptAsAlterTable(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"ALTER TABLE [{schema}].[{node.Name}]\n    ADD [ColumnName] NVARCHAR(50) NULL";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate CREATE TABLE script from column metadata.</summary>
    public async Task ScriptTableAsCreateAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var sql = DatabaseService.GenerateCreateTableScript(schema, node.Name, columns);
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Script column as SELECT with this column.</summary>
    public void ScriptColumnAsSelect(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT [{node.Name}] FROM [{schema}].[{table}]";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Script column as WHERE clause.</summary>
    public void ScriptColumnAsWhere(ObjectExplorerNode node)
    {
        var sql = $"WHERE [{node.Name}] = ''";
        InsertAtCursorRequested?.Invoke(sql);
    }

    /// <summary>Copy column name to clipboard (handled via event).</summary>
    public event Action<string>? CopyToClipboardRequested;

    public void CopyColumnName(ObjectExplorerNode node)
    {
        CopyToClipboardRequested?.Invoke(node.Name);
    }

    public async Task GenerateExecAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var parameters = connStr != null
            ? await _db.GetProcParametersAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetProcParametersAsync(node.DatabaseName, schema, node.Name);

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

    public void SelectSequenceValue(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT current_value FROM [{node.DatabaseName}].sys.sequences WHERE name = '{node.Name}' AND schema_id = SCHEMA_ID('{schema}')";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void ScriptSequenceCreate(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var dataType = node.TypeInfo.Split(',')[0].Trim();
        var sql = $"CREATE SEQUENCE [{schema}].[{node.Name}] AS {dataType}";
        InsertTextRequested?.Invoke(sql, false);
    }

    public async Task ViewJobStepsAsync(ObjectExplorerNode node)
    {
        var connStr = ResolveConnectionString(node);
        var steps = connStr != null
            ? await _db.GetJobStepsAsync(connStr, node.Name)
            : await _db.GetJobStepsAsync(node.Name);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Job Steps: {node.Name}");
        sb.AppendLine($"-- {steps.Count} step(s)");
        sb.AppendLine();

        foreach (var (stepId, stepName, subsystem, command) in steps)
        {
            sb.AppendLine($"-- Step {stepId}: {stepName} [{subsystem}]");
            sb.AppendLine(command);
            sb.AppendLine();
        }

        InsertTextRequested?.Invoke(sb.ToString(), false);
    }

    public async Task ViewJobHistoryAsync(ObjectExplorerNode node)
    {
        var connStr = ResolveConnectionString(node);
        var history = connStr != null
            ? await _db.GetJobHistoryAsync(connStr, node.Name)
            : await _db.GetJobHistoryAsync(node.Name);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Job History: {node.Name} (last {history.Count} runs)");
        sb.AppendLine();

        foreach (var (runStatus, runDate, durationSeconds, message) in history)
        {
            var status = runStatus switch { 0 => "Failed", 1 => "Success", 2 => "Retry", 3 => "Cancelled", _ => "Unknown" };
            var duration = TimeSpan.FromSeconds(durationSeconds);
            sb.AppendLine($"-- {runDate:yyyy-MM-dd HH:mm:ss}  {status}  Duration: {duration:hh\\:mm\\:ss}");
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.AppendLine($"--   {message}");
            }
            sb.AppendLine();
        }

        InsertTextRequested?.Invoke(sb.ToString(), false);
    }

    public void EditData(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 200 * FROM [{schema}].[{node.Name}]";
        EditDataRequested?.Invoke(sql);
    }

    /// <summary>
    /// Restore OE tree from cached nodes (for fast tab-switch).
    /// </summary>
    public void RestoreNodes(List<ObjectExplorerNode> nodes)
    {
        RootNodes.Clear();
        foreach (var node in nodes)
            RootNodes.Add(node);
    }
}
