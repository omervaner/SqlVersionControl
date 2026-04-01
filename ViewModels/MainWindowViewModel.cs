using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    private DispatcherTimer? _autoSyncTimer;
    private bool _autoSyncing;                           // Guard against overlapping syncs

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Not connected";

    [ObservableProperty]
    private bool _showHistoryEmptyState;

    [ObservableProperty]
    private string _connectionDisplay = "Disconnected";

    [ObservableProperty]
    private string _connectionColor = "#88a1bb";

    // --- History view owns its connection ---
    [ObservableProperty]
    private string _historyConnectionDisplay = "Disconnected";

    [ObservableProperty]
    private string _historyConnectionColor = "#88a1bb";

    [ObservableProperty]
    private SavedConnection? _historyConnectionProfile;

    // --- Plan view owns its connection ---
    [ObservableProperty]
    private string _planConnectionDisplay = "Disconnected";

    [ObservableProperty]
    private string _planConnectionColor = "#88a1bb";

    [ObservableProperty]
    private SavedConnection? _planConnectionProfile;

    // --- Activity view owns its connection ---
    [ObservableProperty]
    private string _activityConnectionDisplay = "Disconnected";

    [ObservableProperty]
    private string _activityConnectionColor = "#88a1bb";

    [ObservableProperty]
    private SavedConnection? _activityConnectionProfile;

    [ObservableProperty]
    private bool _isQueryEditorActive;

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
    public SettingsService? AppSettings { get; set; }

    private string? GetDdlSource()
    {
        var s = AppSettings?.Settings;
        if (s == null || !s.IsDdlAuditConfigured) return null;
        var db = s.DdlAuditDatabase!;
        var table = s.DdlAuditTable!;
        // If server is specified and different from current, we can't cross-server query — just use db.table
        return $"{db}.{(table.Contains('.') ? table : $"dbo.{table}")}";
    }

    // Event for rollback confirmation (View subscribes to this)
    public event Func<ObjectVersion, Task<bool>>? RollbackRequested;

    public void OnConnected(ConnectionSettings settings, SavedConnection? profile = null)
    {
        _db.SetConnection(settings);
        IsConnected = true;
        StatusMessage = $"Connected to {settings.Server}/{settings.Database} - loading...";
        var login = settings.UseWindowsAuth ? "Windows" : settings.Username;
        var display = profile?.Name != null
            ? $"{profile.Name} / {settings.Database} ({login})"
            : $"{settings.Server} / {settings.Database} ({login})";
        var color = profile?.Color ?? "#88a1bb";

        ConnectionDisplay = display;
        ConnectionColor = color;

        // Stamp History and Plan with this connection (they remember it independently)
        HistoryConnectionDisplay = display;
        HistoryConnectionColor = color;
        HistoryConnectionProfile = profile;
        PlanConnectionDisplay = display;
        PlanConnectionColor = color;
        PlanConnectionProfile = profile;
        ActivityConnectionDisplay = display;
        ActivityConnectionColor = color;
        ActivityConnectionProfile = profile;

        SelectedDatabase = settings.Database;
        _ = LoadDataAsync();
        StartAutoSyncTimer();
    }

    /// <summary>
    /// Change the History view's connection independently.
    /// </summary>
    public void SetHistoryConnection(ConnectionSettings settings, SavedConnection? profile)
    {
        _db.SetConnection(settings);
        var login = settings.UseWindowsAuth ? "Windows" : settings.Username;
        HistoryConnectionDisplay = profile?.Name != null
            ? $"{profile.Name} / {settings.Database} ({login})"
            : $"{settings.Server} / {settings.Database} ({login})";
        HistoryConnectionColor = profile?.Color ?? "#88a1bb";
        HistoryConnectionProfile = profile;
        SelectedDatabase = settings.Database;
        _ = LoadDataAsync();
    }

    /// <summary>
    /// Change the Activity view's connection independently.
    /// </summary>
    public void SetActivityConnection(ConnectionSettings settings, SavedConnection? profile)
    {
        var login = settings.UseWindowsAuth ? "Windows" : settings.Username;
        ActivityConnectionDisplay = profile?.Name != null
            ? $"{profile.Name} / {settings.Database} ({login})"
            : $"{settings.Server} / {settings.Database} ({login})";
        ActivityConnectionColor = profile?.Color ?? "#88a1bb";
        ActivityConnectionProfile = profile;
    }

    private void StartAutoSyncTimer()
    {
        _autoSyncTimer?.Stop();
        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoSyncTimer.Tick += async (_, _) => await AutoSyncTickAsync();
        _autoSyncTimer.Start();
    }

    public void StopAutoSyncTimer()
    {
        _autoSyncTimer?.Stop();
        _autoSyncTimer = null;
    }

    private async Task AutoSyncTickAsync()
    {
        if (!IsConnected || _autoSyncing || string.IsNullOrEmpty(SelectedDatabase))
            return;

        _autoSyncing = true;
        try
        {
            var ddlSource = GetDdlSource();
            if (ddlSource == null) return;
            var synced = await _db.SyncFromDdlLogAsync(SelectedDatabase, ddlSource);
            if (synced <= 0) return;

            // Reload recent changes without resetting user's current selection
            var savedChange = SelectedChange;
            var changes = await _db.GetRecentChangesAsync(SelectedDatabase);
            RecentChanges.Clear();
            foreach (var c in changes)
                RecentChanges.Add(c);

            // Restore selection if it still exists
            if (savedChange != null)
                SelectedChange = RecentChanges.FirstOrDefault(c => c.VersionId == savedChange.VersionId);

            // Reload objects list without resetting selection
            var savedObject = SelectedObject;
            _allObjects = await _db.GetObjectsAsync(SelectedDatabase);
            if (!IsDependencyMode)
                FilterObjects();

            if (savedObject != null)
                SelectedObject = Objects.FirstOrDefault(o => o.FullName == savedObject.FullName);

            StatusMessage = $"{synced} new change{(synced == 1 ? "" : "s")} synced";
        }
        catch
        {
            // Silently ignore — transient DB errors shouldn't kill the timer
        }
        finally
        {
            _autoSyncing = false;
        }
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // Ensure schema exists (must complete first — other operations need the tables)
            await _db.EnsureSchemaAsync();

            // Run DDL sync and database list fetch in parallel — they're independent
            StatusMessage = "Loading...";
            var savedDb = SelectedDatabase;
            var ddlSource = GetDdlSource();
            var synced = 0;

            if (ddlSource != null)
            {
                var syncTask = _db.SyncFromDdlLogAsync(SelectedDatabase, ddlSource);
                var dbsTask = _db.GetDatabasesAsync();
                await Task.WhenAll(syncTask, dbsTask);
                synced = syncTask.Result;

                var dbs = dbsTask.Result;
                Databases.Clear();
                foreach (var db in dbs) Databases.Add(db);
            }
            else
            {
                var dbs = await _db.GetDatabasesAsync();
                Databases.Clear();
                foreach (var db in dbs) Databases.Add(db);
                ShowHistoryEmptyState = true;
                StatusMessage = "Version tracking: not configured (Settings → Version History)";
            }

            if (synced > 0)
                StatusMessage = $"Synced {synced} new changes from DDL log";

            // Restore database selection
            if (savedDb != null && Databases.Contains(savedDb))
                SelectedDatabase = savedDb;
            if (SelectedDatabase == null && Databases.Contains("AAD"))
                SelectedDatabase = "AAD";

            // Load recent changes and objects in parallel
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
        var ddlSource = GetDdlSource();
        if (ddlSource == null)
        {
            StatusMessage = "Version tracking: not configured (Settings → Version History)";
            return;
        }
        StatusMessage = "Syncing from DDL log...";
        try
        {
            var synced = await _db.SyncFromDdlLogAsync(SelectedDatabase, ddlSource);
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

        // Run recent changes and objects fetch in parallel — they're independent
        var changesTask = _db.GetRecentChangesAsync(SelectedDatabase);
        var objectsTask = SelectedDatabase != null
            ? _db.GetObjectsAsync(SelectedDatabase)
            : Task.FromResult(new List<DatabaseObject>());

        await Task.WhenAll(changesTask, objectsTask);

        RecentChanges.Clear();
        foreach (var c in changesTask.Result)
            RecentChanges.Add(c);

        if (SelectedDatabase != null)
        {
            _allObjects = objectsTask.Result;
            FilterObjects();
        }

        ShowHistoryEmptyState = RecentChanges.Count == 0;
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
        try { _codeSearchCts?.Cancel(); } catch (ObjectDisposedException) { }
        _codeSearchCts?.Dispose();
        _codeSearchCts = null;

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
        var (success, error) = await _db.RollbackToVersionAsync(LeftVersion);

        if (success)
        {
            StatusMessage = $"Successfully rolled back to v{LeftVersion.VersionNumber}";
            await RefreshAsync();
        }
        else
        {
            StatusMessage = $"Rollback failed: {error ?? "unknown error"}";
        }
    }
}
