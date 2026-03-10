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
    private CancellationTokenSource? _codeSearchCts;    // Debounce for code search
    private string _activeCodeSearchTerm = "";           // The term that produced current code results

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

    /// <summary>
    /// The current search term used for highlighting in the diff panel (set when a code match is selected).
    /// </summary>
    [ObservableProperty]
    private string _highlightTerm = "";

    // --- Dependency mode ---
    [ObservableProperty]
    private bool _isDependencyMode;

    [ObservableProperty]
    private string _dependencySourceName = "";

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

            // Load databases (save/restore selection since Clear() nulls the ComboBox binding)
            var savedDb = SelectedDatabase;
            var dbs = await _db.GetDatabasesAsync();
            Databases.Clear();
            foreach (var db in dbs)
            {
                Databases.Add(db);
            }
            if (savedDb != null && Databases.Contains(savedDb))
                SelectedDatabase = savedDb;

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
        // Immediate name filter
        FilterObjects();

        // Cancel any pending code search
        _codeSearchCts?.Cancel();

        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || string.IsNullOrEmpty(SelectedDatabase))
        {
            _activeCodeSearchTerm = "";
            HighlightTerm = "";
            return;
        }

        // Debounced async code search
        _codeSearchCts = new CancellationTokenSource();
        var token = _codeSearchCts.Token;
        var searchTerm = value;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (token.IsCancellationRequested) return;

                var results = await _db.SearchObjectDefinitionsAsync(SelectedDatabase!, searchTerm, false);
                if (token.IsCancellationRequested) return;

                // Get names already in the Object Browser (name matches)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;

                    var existingNames = new HashSet<string>(
                        Objects.Select(o => o.FullName), StringComparer.OrdinalIgnoreCase);

                    var codeOnlyMatches = 0;
                    foreach (var result in results)
                    {
                        var fullName = $"{result.SchemaName}.{result.ObjectName}";
                        if (existingNames.Contains(fullName)) continue;

                        Objects.Add(new DatabaseObject
                        {
                            DatabaseName = SelectedDatabase!,
                            SchemaName = result.SchemaName,
                            ObjectName = result.ObjectName,
                            ObjectType = result.ObjectType,
                            IsCodeMatch = true,
                            // Store the definition in a tag for later retrieval
                            CodeMatchDefinition = result.Definition
                        });
                        codeOnlyMatches++;
                    }

                    _activeCodeSearchTerm = searchTerm;

                    if (codeOnlyMatches > 0)
                    {
                        StatusMessage = $"{Objects.Count} objects ({codeOnlyMatches} found in code)";
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Don't overwrite status for stale searches
                    if (!token.IsCancellationRequested)
                        StatusMessage = $"Code search error: {ex.Message}";
                });
            }
        }, token);
    }

    private void FilterObjects()
    {
        // Exit dependency mode when user searches
        if (IsDependencyMode)
        {
            IsDependencyMode = false;
            DependencySourceName = "";
        }

        Objects.Clear();
        _activeCodeSearchTerm = "";

        IEnumerable<DatabaseObject> filtered;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = _allObjects;
        }
        else
        {
            // Split search into words, match all of them (space/underscore insensitive)
            var searchTerms = SearchText.Replace("_", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
            o.IsCodeMatch = false; // Reset in case reused
            Objects.Add(o);
        }
    }

    partial void OnSelectedChangeChanged(RecentChange? value)
    {
        if (value != null)
        {
            HighlightTerm = "";
            _ = LoadVersionsForChangeAsync(value);
        }
    }

    partial void OnSelectedObjectChanged(DatabaseObject? value)
    {
        if (value != null && !value.IsSectionHeader)
        {
            HighlightTerm = value.IsCodeMatch ? _activeCodeSearchTerm : "";
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
        else if (versions.Count == 0 && !string.IsNullOrEmpty(obj.CodeMatchDefinition))
        {
            LeftVersion = null;
            RightVersion = null;
            LeftCode = "";
            RightCode = obj.CodeMatchDefinition;
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
    private async Task CopyLeftCodeAsync()
    {
        if (!string.IsNullOrEmpty(LeftCode))
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(LeftCode);
                StatusMessage = "Left definition copied to clipboard";
            }
        }
    }

    [RelayCommand]
    private async Task CopyRightCodeAsync()
    {
        if (!string.IsNullOrEmpty(RightCode))
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(RightCode);
                StatusMessage = "Right definition copied to clipboard";
            }
        }
    }

    public async Task ShowDependenciesAsync(DatabaseObject obj)
    {
        if (string.IsNullOrEmpty(SelectedDatabase)) return;

        IsDependencyMode = true;
        DependencySourceName = obj.FullName;
        HighlightTerm = obj.ObjectName;
        StatusMessage = $"Loading dependencies for {obj.FullName}...";

        Objects.Clear();
        SelectedObject = null;

        try
        {
            var (uses, usedBy) = await _db.GetDependenciesAsync(
                SelectedDatabase, obj.SchemaName, obj.ObjectName);

            // Section header: Uses
            Objects.Add(new DatabaseObject
            {
                IsSectionHeader = true,
                SectionTitle = $"Uses ({uses.Count})",
                ObjectName = $"Uses ({uses.Count})"
            });

            foreach (var item in uses)
            {
                Objects.Add(new DatabaseObject
                {
                    DatabaseName = SelectedDatabase,
                    SchemaName = item.SchemaName,
                    ObjectName = item.ObjectName,
                    ObjectType = item.ObjectType,
                    DependencyDirection = "Uses",
                    IsCodeMatch = true,
                    CodeMatchDefinition = item.Definition
                });
            }

            // Section header: Used By
            Objects.Add(new DatabaseObject
            {
                IsSectionHeader = true,
                SectionTitle = $"Used By ({usedBy.Count})",
                ObjectName = $"Used By ({usedBy.Count})"
            });

            foreach (var item in usedBy)
            {
                Objects.Add(new DatabaseObject
                {
                    DatabaseName = SelectedDatabase,
                    SchemaName = item.SchemaName,
                    ObjectName = item.ObjectName,
                    ObjectType = item.ObjectType,
                    DependencyDirection = "Used By",
                    IsCodeMatch = true,
                    CodeMatchDefinition = item.Definition
                });
            }

            var total = uses.Count + usedBy.Count;
            StatusMessage = total == 0
                ? $"No dependencies found for {obj.FullName}"
                : $"{uses.Count} outgoing, {usedBy.Count} incoming";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Dependency error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BackFromDependencies()
    {
        IsDependencyMode = false;
        DependencySourceName = "";
        HighlightTerm = "";
        FilterObjects();
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
