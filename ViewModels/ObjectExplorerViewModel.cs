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
    private CancellationTokenSource? _searchCts;
    private List<ObjectExplorerNode>? _savedFilterNodes;
    private bool _isServerSearchActive;

    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _rootNodes = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _showFilterEmptyState;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _showEmptyState;

    /// <summary>Fired when a context menu action wants to set editor text. (sql, autoRun, databaseName, connectionId)</summary>
    public event Action<string, bool, string?, string?>? InsertTextRequested;

    /// <summary>Fired when column double-click wants to insert text at cursor.</summary>
    public event Action<string>? InsertAtCursorRequested;

    /// <summary>Fired when "Edit Data" wants to run a SELECT and auto-enter edit mode. (sql, databaseName, connectionId)</summary>
    public event Action<string, string?, string?>? EditDataRequested;

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
        RootNodes.CollectionChanged += (_, _) => ShowEmptyState = RootNodes.Count == 0;
    }

    partial void OnRootNodesChanged(ObservableCollection<ObjectExplorerNode> value)
    {
        ShowEmptyState = value.Count == 0;
        value.CollectionChanged += (_, _) => ShowEmptyState = RootNodes.Count == 0;
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
            Dispatcher.UIThread.Post(() => _ = ApplyFilterAsync()), null, 200, Timeout.Infinite);
    }

    private async Task ApplyFilterAsync()
    {
        var filter = FilterText?.Trim() ?? "";

        // Cancel any in-flight search immediately
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        // Empty or single char: restore original tree and apply client-side filter
        if (filter.Length < 2)
        {
            RestoreFromServerSearch();
            if (string.IsNullOrEmpty(filter))
            {
                // Clear all visibility flags
                foreach (var node in RootNodes)
                    ApplyFilterToNode(node, "");
                ShowFilterEmptyState = false;
            }
            else
            {
                // Single char: client-side only on loaded nodes
                ApplyClientSideFilter(filter);
            }
            return;
        }

        // 2+ chars: server-side search
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        IsSearching = true;
        ShowFilterEmptyState = false;

        try
        {
            // Save the original tree before first server search
            if (!_isServerSearchActive)
            {
                _savedFilterNodes = RootNodes.ToList();
                _isServerSearchActive = true;
            }

            // Gather all connections and their databases
            var searchTasks = new List<Task<List<(string Database, string Schema, string Name, string TypeCode)>>>();
            var connectionInfos = new List<(string ConnId, string? Color, string? ConnName)>();

            if (_registry != null)
            {
                foreach (var managed in _registry.ActiveConnections)
                {
                    if (managed.ResolvedConnectionString == null || managed.Databases == null) continue;
                    ct.ThrowIfCancellationRequested();

                    connectionInfos.Add((managed.Id, managed.Color, managed.Config.Name ?? managed.Config.Server));
                    searchTasks.Add(_db.SearchObjectsAsync(
                        managed.ResolvedConnectionString, managed.Databases, filter, ct));
                }
            }
            else if (_activeConnectionString != null)
            {
                // Legacy single-connection mode: search databases from current tree
                var databases = _savedFilterNodes?
                    .SelectMany(n => n.Children)
                    .Where(n => n.NodeType == ObjectExplorerNodeType.Database)
                    .Select(n => n.Name)
                    .ToList() ?? [];

                if (databases.Count > 0)
                {
                    connectionInfos.Add(("", null, null));
                    searchTasks.Add(_db.SearchObjectsAsync(
                        _activeConnectionString, databases, filter, ct));
                }
            }

            if (searchTasks.Count == 0)
            {
                IsSearching = false;
                ShowFilterEmptyState = true;
                return;
            }

            var allResults = await Task.WhenAll(searchTasks);
            ct.ThrowIfCancellationRequested();

            // Build filtered tree from results
            BuildFilteredTree(connectionInfos, allResults, ct);
        }
        catch (OperationCanceledException)
        {
            // Search was superseded by a newer one — do nothing
        }
        catch (Exception ex)
        {
            // Server error — fall back to client-side filter on original tree
            AppLogger.LogError("ObjectExplorer.ServerSearch", ex);
            RestoreFromServerSearch();
            ApplyClientSideFilter(filter);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsSearching = false;
        }
    }

    private void BuildFilteredTree(
        List<(string ConnId, string? Color, string? ConnName)> connectionInfos,
        List<(string Database, string Schema, string Name, string TypeCode)>[] allResults,
        CancellationToken ct)
    {
        var newRoots = new ObservableCollection<ObjectExplorerNode>();

        for (int i = 0; i < connectionInfos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (connId, color, connName) = connectionInfos[i];
            var results = allResults[i];
            if (results.Count == 0) continue;

            // Group by database, then by type
            var byDatabase = results.GroupBy(r => r.Database);

            // Find the original connection node (for its properties)
            ObjectExplorerNode? origConn = null;
            if (!string.IsNullOrEmpty(connId))
                origConn = _savedFilterNodes?.FirstOrDefault(n => n.ConnectionId == connId);

            var connNode = new ObjectExplorerNode
            {
                Name = connName ?? "Connection",
                NodeType = ObjectExplorerNodeType.Connection,
                ConnectionId = connId,
                ConnectionColor = color,
                IsExpanded = true,
                IsVisibleInFilter = true
            };

            foreach (var dbGroup in byDatabase)
            {
                ct.ThrowIfCancellationRequested();
                var dbNode = new ObjectExplorerNode
                {
                    Name = dbGroup.Key,
                    NodeType = ObjectExplorerNodeType.Database,
                    DatabaseName = dbGroup.Key,
                    ConnectionId = connId,
                    ConnectionColor = color,
                    IsExpanded = true,
                    IsVisibleInFilter = true
                };

                var byType = dbGroup.GroupBy(r => TypeCodeToCategory(r.TypeCode));
                foreach (var typeGroup in byType)
                {
                    var folderNode = new ObjectExplorerNode
                    {
                        Name = typeGroup.Key,
                        NodeType = ObjectExplorerNodeType.Folder,
                        DatabaseName = dbGroup.Key,
                        ConnectionId = connId,
                        IsExpanded = true,
                        IsVisibleInFilter = true,
                        ChildCount = typeGroup.Count()
                    };

                    foreach (var obj in typeGroup)
                    {
                        var objNode = new ObjectExplorerNode
                        {
                            Name = obj.Name,
                            Schema = obj.Schema,
                            DatabaseName = dbGroup.Key,
                            NodeType = TypeCodeToNodeType(obj.TypeCode),
                            ConnectionId = connId,
                            ConnectionColor = color,
                            IsVisibleInFilter = true
                        };
                        WireNode(objNode);
                        folderNode.Children.Add(objNode);
                    }

                    dbNode.Children.Add(folderNode);
                }

                dbNode.ChildCount = dbGroup.Count();
                connNode.Children.Add(dbNode);
            }

            connNode.ChildCount = connNode.Children.Sum(db => db.ChildCount);
            WireNode(connNode);
            newRoots.Add(connNode);
        }

        RootNodes = newRoots;
        ShowFilterEmptyState = newRoots.Count == 0;
    }

    private void RestoreFromServerSearch()
    {
        if (!_isServerSearchActive) return;
        _isServerSearchActive = false;

        if (_savedFilterNodes != null)
        {
            RootNodes = new ObservableCollection<ObjectExplorerNode>(_savedFilterNodes);
            _savedFilterNodes = null;
        }
    }

    /// <summary>Client-side filter on the currently loaded tree (for short filters or fallback).</summary>
    private void ApplyClientSideFilter(string filter)
    {
        var anyVisible = false;
        foreach (var node in RootNodes)
        {
            if (ApplyFilterToNode(node, filter))
                anyVisible = true;
        }
        ShowFilterEmptyState = !string.IsNullOrEmpty(filter) && !anyVisible;
    }

    /// <summary>Apply client-side visibility filter to the in-memory tree.</summary>
    public void ApplyFilter()
    {
        ApplyClientSideFilter(FilterText?.Trim() ?? "");
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

    private static string TypeCodeToCategory(string typeCode) => typeCode switch
    {
        "U" => "Tables",
        "V" => "Views",
        "P" => "Stored Procedures",
        "FN" or "TF" or "IF" => "Functions",
        "SO" => "Sequences",
        "TR" => "Triggers",
        _ => "Other"
    };

    private static ObjectExplorerNodeType TypeCodeToNodeType(string typeCode) => typeCode switch
    {
        "U" => ObjectExplorerNodeType.Table,
        "V" => ObjectExplorerNodeType.View,
        "P" => ObjectExplorerNodeType.Proc,
        "FN" or "TF" or "IF" => ObjectExplorerNodeType.Function,
        "SO" => ObjectExplorerNodeType.Sequence,
        "TR" => ObjectExplorerNodeType.Trigger,
        _ => ObjectExplorerNodeType.Folder
    };

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
