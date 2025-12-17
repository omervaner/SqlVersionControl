using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DatabaseService _db;
    private List<DatabaseObject> _allObjects = new();  // Unfiltered list for search

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Not connected";

    [ObservableProperty]
    private string? _selectedDatabase;

    [ObservableProperty]
    private ObservableCollection<string> _databases = new();

    [ObservableProperty]
    private ObservableCollection<RecentChange> _recentChanges = new();

    [ObservableProperty]
    private ObservableCollection<DatabaseObject> _objects = new();

    [ObservableProperty]
    private ObservableCollection<ObjectVersion> _versions = new();

    [ObservableProperty]
    private RecentChange? _selectedChange;

    [ObservableProperty]
    private DatabaseObject? _selectedObject;

    [ObservableProperty]
    private ObjectVersion? _leftVersion;

    [ObservableProperty]
    private ObjectVersion? _rightVersion;

    [ObservableProperty]
    private string _leftCode = "";

    [ObservableProperty]
    private string _rightCode = "";

    [ObservableProperty]
    private SideBySideDiffModel? _diffModel;

    [ObservableProperty]
    private string _searchText = "";

    public MainWindowViewModel()
    {
        _db = new DatabaseService();
    }

    public MainWindowViewModel(DatabaseService db)
    {
        _db = db;
    }

    public DatabaseService DatabaseService => _db;

    // Event for rollback confirmation (View subscribes to this)
    public event Func<ObjectVersion, Task<bool>>? RollbackRequested;

    public void OnConnected(ConnectionSettings settings)
    {
        _db.SetConnection(settings);
        IsConnected = true;
        StatusMessage = $"Connected to {settings.Server}/{settings.Database} - loading...";
        SelectedDatabase = settings.Database;
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // Ensure schema exists
            await _db.EnsureSchemaAsync();

            // Sync from DDL log
            StatusMessage = "Syncing from DDL log...";
            var synced = await _db.SyncFromDdlLogAsync(SelectedDatabase);
            if (synced > 0)
            {
                StatusMessage = $"Synced {synced} new changes from DDL log";
            }

            // Load databases
            var dbs = await _db.GetDatabasesAsync();
            Databases.Clear();
            foreach (var db in dbs)
            {
                Databases.Add(db);
            }

            // Load recent changes
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        StatusMessage = "Syncing from DDL log...";
        try
        {
            var synced = await _db.SyncFromDdlLogAsync(SelectedDatabase);
            StatusMessage = synced > 0
                ? $"Synced {synced} new changes"
                : "No new changes to sync";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusMessage = "Loading...";

        var changes = await _db.GetRecentChangesAsync(SelectedDatabase);
        RecentChanges.Clear();
        foreach (var c in changes)
        {
            RecentChanges.Add(c);
        }

        if (SelectedDatabase != null)
        {
            _allObjects = await _db.GetObjectsAsync(SelectedDatabase);
            FilterObjects();
        }

        StatusMessage = $"Loaded {RecentChanges.Count} recent changes";
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (value != null)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterObjects();
    }

    private void FilterObjects()
    {
        Objects.Clear();

        IEnumerable<DatabaseObject> filtered;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = _allObjects;
        }
        else
        {
            // Split search into words, match all of them (space/underscore insensitive)
            var searchTerms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = _allObjects.Where(o =>
            {
                var name = o.ObjectName.Replace("_", " ");
                var schema = o.SchemaName.Replace("_", " ");
                return searchTerms.All(term =>
                    name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    schema.Contains(term, StringComparison.OrdinalIgnoreCase));
            });
        }

        foreach (var o in filtered)
        {
            Objects.Add(o);
        }
    }

    partial void OnSelectedChangeChanged(RecentChange? value)
    {
        if (value != null)
        {
            _ = LoadVersionsForChangeAsync(value);
        }
    }

    partial void OnSelectedObjectChanged(DatabaseObject? value)
    {
        if (value != null)
        {
            _ = LoadVersionsAsync(value);
        }
    }

    private async Task LoadVersionsForChangeAsync(RecentChange change)
    {
        var versions = await _db.GetObjectHistoryAsync(
            SelectedDatabase ?? "", change.SchemaName, change.ObjectName);

        Versions.Clear();
        foreach (var v in versions)
        {
            Versions.Add(v);
        }

        // Auto-select this version and previous for diff
        var current = versions.FirstOrDefault(v => v.VersionId == change.VersionId);
        var previous = versions.FirstOrDefault(v => v.VersionNumber == change.VersionNumber - 1);

        if (current != null)
        {
            RightVersion = current;
            RightCode = current.Definition;
        }

        if (previous != null)
        {
            LeftVersion = previous;
            LeftCode = previous.Definition;
        }
        else if (current != null)
        {
            LeftVersion = null;
            LeftCode = "";
        }

        UpdateDiff();
    }

    private async Task LoadVersionsAsync(DatabaseObject obj)
    {
        var versions = await _db.GetObjectHistoryAsync(
            obj.DatabaseName, obj.SchemaName, obj.ObjectName);

        Versions.Clear();
        foreach (var v in versions)
        {
            Versions.Add(v);
        }

        // Select latest two for diff
        if (versions.Count >= 2)
        {
            LeftVersion = versions[1];
            RightVersion = versions[0];
            LeftCode = versions[1].Definition;
            RightCode = versions[0].Definition;
        }
        else if (versions.Count == 1)
        {
            RightVersion = versions[0];
            RightCode = versions[0].Definition;
            LeftVersion = null;
            LeftCode = "";
        }

        UpdateDiff();
    }

    partial void OnLeftVersionChanged(ObjectVersion? value)
    {
        if (value != null)
        {
            LeftCode = value.Definition;
            UpdateDiff();
        }
    }

    partial void OnRightVersionChanged(ObjectVersion? value)
    {
        if (value != null)
        {
            RightCode = value.Definition;
            UpdateDiff();
        }
    }

    private void UpdateDiff()
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        DiffModel = diffBuilder.BuildDiffModel(LeftCode, RightCode);
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (LeftVersion == null) return;

        // Ask for confirmation via the View
        if (RollbackRequested != null)
        {
            var confirmed = await RollbackRequested(LeftVersion);
            if (!confirmed) return;
        }

        StatusMessage = "Executing rollback...";
        var success = await _db.RollbackToVersionAsync(LeftVersion);

        if (success)
        {
            StatusMessage = $"Successfully rolled back to v{LeftVersion.VersionNumber}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = "Rollback failed - check permissions";
        }
    }
}
