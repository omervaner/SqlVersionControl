using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class CompareViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private List<CompareObject> _allObjects = new();

    // Table structure comparison
    private List<TableCompareResult> _tableCompareResults = new();

    // Scan/deploy cancellation
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _deployCts;

    // Store passwords temporarily for non-Windows auth (not persisted to disk)
    private readonly Dictionary<string, string> _passwords = new();

    // Source connection
    [ObservableProperty]
    private ObservableCollection<SavedConnection> _sourceConnections = new();

    [ObservableProperty]
    private SavedConnection? _selectedSourceConnection;

    [ObservableProperty]
    private string _sourceStatus = "Not connected";

    [ObservableProperty]
    private bool _isSourceConnected;

    private string _sourceConnectionString = "";

    // Target connection
    [ObservableProperty]
    private ObservableCollection<SavedConnection> _targetConnections = new();

    [ObservableProperty]
    private SavedConnection? _selectedTargetConnection;

    [ObservableProperty]
    private string _targetStatus = "Not connected";

    [ObservableProperty]
    private bool _isTargetConnected;

    private string _targetConnectionString = "";

    // Target2 connection (optional third database for three-way compare)
    [ObservableProperty]
    private ObservableCollection<SavedConnection> _target2Connections = new();

    [ObservableProperty]
    private SavedConnection? _selectedTarget2Connection;

    [ObservableProperty]
    private string _target2Status = "Not connected";

    [ObservableProperty]
    private bool _isTarget2Connected;

    [ObservableProperty]
    private bool _showTarget2; // Toggle for showing third DB

    public string ToggleTarget2ButtonText => ShowTarget2 ? "- T2" : "+ T2";

    partial void OnShowTarget2Changed(bool value)
    {
        OnPropertyChanged(nameof(ToggleTarget2ButtonText));
    }

    partial void OnIsTableCompareModeChanged(bool value)
    {
        // Disable T2 in table mode
        if (value && ShowTarget2)
        {
            ToggleTarget2();
        }

        // Reload with new mode
        if (IsSourceConnected || IsTargetConnected)
        {
            _ = LoadObjectsAsync();
        }
    }

    private string _target2ConnectionString = "";

    // Objects and comparison
    [ObservableProperty]
    private ObservableCollection<CompareObject> _objects = new();

    [ObservableProperty]
    private CompareObject? _selectedObject;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _sourceCode = "";

    [ObservableProperty]
    private string _targetCode = "";

    [ObservableProperty]
    private SideBySideDiffModel? _diffModel;

    // Second diff for Target1 ↔ Target2 comparison
    [ObservableProperty]
    private string _target2Code = "";

    [ObservableProperty]
    private SideBySideDiffModel? _diffModel2;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _canDeploy;

    [ObservableProperty]
    private bool _canDeploy2; // Can deploy from Target1 to Target2

    // Table compare mode
    [ObservableProperty]
    private bool _isTableCompareMode;

    [ObservableProperty]
    private ObservableCollection<ColumnCompareResult> _tableCompareColumns = new();

    // Data compare mode (row-level comparison within Tables mode)
    [ObservableProperty]
    private bool _isDataCompareMode;

    [ObservableProperty]
    private DataCompareResult? _dataCompareResult;

    [ObservableProperty]
    private ObservableCollection<DataCompareRow> _dataCompareRows = new();

    [ObservableProperty]
    private ObservableCollection<DataCompareRow> _filteredDataRows = new();

    [ObservableProperty]
    private DataCompareRow? _selectedDataRow;

    [ObservableProperty]
    private ObservableCollection<DataCompareField> _selectedRowFields = new();

    [ObservableProperty]
    private string _dataCompareSummary = "";

    [ObservableProperty]
    private bool _isDataLoading;

    [ObservableProperty]
    private string _dataFilterColumn = "";

    [ObservableProperty]
    private string _dataFilterText = "";

    [ObservableProperty]
    private ObservableCollection<string> _dataFilterColumns = new();

    // Show only differences feature
    [ObservableProperty]
    private bool _showOnlyDifferences;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanProgressText = "";

    // Summary counts
    [ObservableProperty]
    private int _sourceOnlyCount;

    [ObservableProperty]
    private int _targetOnlyCount;

    [ObservableProperty]
    private int _modifiedCount;

    [ObservableProperty]
    private int _identicalCount;

    public bool HasSummary => SourceOnlyCount > 0 || TargetOnlyCount > 0 || ModifiedCount > 0;

    // Selection for batch deploy
    [ObservableProperty]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    // Event for deployment confirmation
    public event Func<string, string, Task<bool>>? DeployRequested;

    // Event for password prompt (returns password or null if cancelled)
    public event Func<SavedConnection, Task<string?>>? PasswordRequested;

    public CompareViewModel() : this(new SettingsService())
    {
    }

    public CompareViewModel(SettingsService settings)
    {
        _settings = settings;
        LoadSavedConnections();
        // Restore dropdown selections visually (no auto-connect)
        // User will click Refresh or select to actually connect
        RestoreSelections();
    }

    private void RestoreSelections()
    {
        var (lastSource, lastTarget) = _settings.GetLastComparison();

        if (lastSource != null)
        {
            // Set backing field directly to avoid triggering connection
            _selectedSourceConnection = SourceConnections.FirstOrDefault(c =>
                c.Server == lastSource.Server && c.Database == lastSource.Database);
        }

        if (lastTarget != null)
        {
            _selectedTargetConnection = TargetConnections.FirstOrDefault(c =>
                c.Server == lastTarget.Server && c.Database == lastTarget.Database);
        }
    }

    /// <summary>
    /// Auto-connect source if we already have credentials (from main app login).
    /// Only connects source, not target - avoids double password prompts.
    /// </summary>
    public async Task TryAutoConnectSourceAsync()
    {
        if (_selectedSourceConnection == null) return;

        // Auto-connect if Windows Auth OR if we already have the password stored
        if (_selectedSourceConnection.UseWindowsAuth || HasPasswordFor(_selectedSourceConnection))
        {
            await ConnectSourceAsync(_selectedSourceConnection);
        }
    }

    private void LoadSavedConnections()
    {
        foreach (var conn in _settings.Settings.RecentConnections)
        {
            SourceConnections.Add(conn);
            TargetConnections.Add(conn);
            Target2Connections.Add(conn);
        }
    }

    private void SaveLastComparison()
    {
        _settings.SaveLastComparison(SelectedSourceConnection, SelectedTargetConnection);
    }

    partial void OnSelectedSourceConnectionChanged(SavedConnection? value)
    {
        if (value != null)
        {
            _ = ConnectSourceAsync(value);
        }
    }

    partial void OnSelectedTargetConnectionChanged(SavedConnection? value)
    {
        if (value != null)
        {
            _ = ConnectTargetAsync(value);
        }
    }

    partial void OnSelectedTarget2ConnectionChanged(SavedConnection? value)
    {
        if (value != null)
        {
            _ = ConnectTarget2Async(value);
        }
    }

    private async Task ConnectSourceAsync(SavedConnection conn)
    {
        SourceStatus = "Connecting...";

        // Check if we need password and don't have it
        if (!conn.UseWindowsAuth && !HasPasswordFor(conn))
        {
            var password = await RequestPasswordAsync(conn);
            if (password == null)
            {
                SourceStatus = "Cancelled";
                return;
            }
            StorePassword(conn, password);
        }

        _sourceConnectionString = BuildConnectionString(conn);

        if (await TestConnectionAsync(_sourceConnectionString))
        {
            IsSourceConnected = true;
            SourceStatus = $"Connected: {conn.Server}/{conn.Database}";
            SaveLastComparison();
            await LoadObjectsAsync();
        }
        else
        {
            IsSourceConnected = false;
            SourceStatus = "Connection failed";
        }
    }

    private async Task ConnectTargetAsync(SavedConnection conn)
    {
        TargetStatus = "Connecting...";

        // Check if we need password and don't have it
        if (!conn.UseWindowsAuth && !HasPasswordFor(conn))
        {
            var password = await RequestPasswordAsync(conn);
            if (password == null)
            {
                TargetStatus = "Cancelled";
                return;
            }
            StorePassword(conn, password);
        }

        _targetConnectionString = BuildConnectionString(conn);

        if (await TestConnectionAsync(_targetConnectionString))
        {
            IsTargetConnected = true;
            TargetStatus = $"Connected: {conn.Server}/{conn.Database}";
            SaveLastComparison();

            // Auto-connect source after target connects (password should be in PasswordStore from main login)
            if (SelectedSourceConnection != null && !IsSourceConnected)
            {
                if (SelectedSourceConnection.UseWindowsAuth || HasPasswordFor(SelectedSourceConnection))
                {
                    await ConnectSourceAsync(SelectedSourceConnection);
                }
            }
            else
            {
                await LoadObjectsAsync();
            }
        }
        else
        {
            IsTargetConnected = false;
            TargetStatus = "Connection failed";
        }
    }

    private async Task ConnectTarget2Async(SavedConnection conn)
    {
        Target2Status = "Connecting...";

        // Check if we need password and don't have it
        if (!conn.UseWindowsAuth && !HasPasswordFor(conn))
        {
            var password = await RequestPasswordAsync(conn);
            if (password == null)
            {
                Target2Status = "Cancelled";
                return;
            }
            StorePassword(conn, password);
        }

        _target2ConnectionString = BuildConnectionString(conn);

        if (await TestConnectionAsync(_target2ConnectionString))
        {
            IsTarget2Connected = true;
            Target2Status = $"Connected: {conn.Server}/{conn.Database}";

            // Reload definitions if we have a selected object
            if (SelectedObject != null)
            {
                await LoadDefinitionsAsync(SelectedObject);
            }
        }
        else
        {
            IsTarget2Connected = false;
            Target2Status = "Connection failed";
        }
    }

    private bool HasPasswordFor(SavedConnection conn)
    {
        // Check global store first
        if (PasswordStore.Has(conn.Server, conn.Database, conn.Username))
            return true;

        // Check local store
        var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
        return _passwords.ContainsKey(key);
    }

    private void StorePassword(SavedConnection conn, string password)
    {
        var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
        _passwords[key] = password;
        // Also persist to global encrypted store
        PasswordStore.Store(conn.Server, conn.Database, conn.Username, password);
        PasswordStore.Save();
    }

    private async Task<string?> RequestPasswordAsync(SavedConnection conn)
    {
        if (PasswordRequested != null)
        {
            return await PasswordRequested(conn);
        }
        return null;
    }

    private string BuildConnectionString(SavedConnection conn)
    {
        var settings = new ConnectionSettings
        {
            Server = conn.Server,
            Database = conn.Database,
            UseWindowsAuth = conn.UseWindowsAuth
        };

        if (!conn.UseWindowsAuth)
        {
            settings.Username = conn.Username;

            // Check global PasswordStore first (from initial login)
            var globalPassword = PasswordStore.Get(conn.Server, conn.Database, conn.Username);
            if (!string.IsNullOrEmpty(globalPassword))
            {
                settings.Password = globalPassword;
            }
            else
            {
                // Then check local passwords (from QuickConnectionDialog)
                var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
                if (_passwords.TryGetValue(key, out var password))
                    settings.Password = password;
                else
                    settings.UseWindowsAuth = true; // No password — fall back to Windows auth
            }
        }

        return settings.ConnectionString;
    }

    private async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadObjectsAsync()
    {
        if (!IsSourceConnected && !IsTargetConnected) return;

        // Remember selected object to restore after refresh
        var selectedFullName = SelectedObject?.FullName;

        _allObjects.Clear();
        _tableCompareResults.Clear();

        if (IsTableCompareMode)
        {
            await LoadTableObjectsAsync();
        }
        else
        {
            await LoadCodeObjectsAsync();
        }

        FilterObjects();

        // Re-select the same object (new instance) and reload definitions
        if (!string.IsNullOrEmpty(selectedFullName))
        {
            var matchingObject = Objects.FirstOrDefault(o => o.FullName == selectedFullName);
            if (matchingObject != null)
            {
                SelectedObject = matchingObject;
                if (IsTableCompareMode)
                    LoadTableColumns(matchingObject);
                else
                    await LoadDefinitionsAsync(matchingObject);
            }
            else
            {
                SelectedObject = null;
            }
        }
    }

    private async Task LoadCodeObjectsAsync()
    {
        var sourceObjects = new Dictionary<string, string>();
        var targetObjects = new Dictionary<string, string>();

        if (IsSourceConnected)
            sourceObjects = await GetObjectsFromDatabaseAsync(_sourceConnectionString);
        if (IsTargetConnected)
            targetObjects = await GetObjectsFromDatabaseAsync(_targetConnectionString);

        var allKeys = sourceObjects.Keys.Union(targetObjects.Keys).OrderBy(k => k);

        foreach (var key in allKeys)
        {
            var parts = key.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "dbo";
            var name = parts.Length > 1 ? parts[1] : parts[0];

            var existsInSource = sourceObjects.ContainsKey(key);
            var existsInTarget = targetObjects.ContainsKey(key);

            _allObjects.Add(new CompareObject
            {
                SchemaName = schema,
                ObjectName = name,
                FullName = key,
                ExistsInSource = existsInSource,
                ExistsInTarget = existsInTarget,
                Status = GetCompareStatus(existsInSource, existsInTarget)
            });
        }
    }

    private async Task LoadTableObjectsAsync()
    {
        var sourceColumns = new List<TableColumnInfo>();
        var targetColumns = new List<TableColumnInfo>();

        try
        {
            if (IsSourceConnected)
            {
                var sourceDb = SelectedSourceConnection!.Database;
                sourceColumns = await DatabaseService.GetTableStructureAsync(_sourceConnectionString, sourceDb);
            }
            if (IsTargetConnected)
            {
                var targetDb = SelectedTargetConnection!.Database;
                targetColumns = await DatabaseService.GetTableStructureAsync(_targetConnectionString, targetDb);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading tables: {ex.Message}";
            return;
        }

        _tableCompareResults = TableCompareService.Compare(sourceColumns, targetColumns);

        // Update summary counts immediately (no scan phase needed for tables)
        SourceOnlyCount = 0;
        TargetOnlyCount = 0;
        ModifiedCount = 0;
        IdenticalCount = 0;

        foreach (var table in _tableCompareResults)
        {
            var status = TableStatusToString(table.Status);
            var existsInSource = table.Status != TableCompareStatus.TargetOnly;
            var existsInTarget = table.Status != TableCompareStatus.SourceOnly;

            _allObjects.Add(new CompareObject
            {
                SchemaName = table.Schema,
                ObjectName = table.TableName,
                FullName = table.TableKey,
                ExistsInSource = existsInSource,
                ExistsInTarget = existsInTarget,
                Status = status,
                HasBeenCompared = true // statuses are final
            });

            switch (table.Status)
            {
                case TableCompareStatus.SourceOnly: SourceOnlyCount++; break;
                case TableCompareStatus.TargetOnly: TargetOnlyCount++; break;
                case TableCompareStatus.Different: ModifiedCount++; break;
                case TableCompareStatus.Match: IdenticalCount++; break;
            }
        }

        OnPropertyChanged(nameof(HasSummary));
    }

    private static string TableStatusToString(TableCompareStatus status) => status switch
    {
        TableCompareStatus.Match => "Identical",
        TableCompareStatus.Different => "Modified",
        TableCompareStatus.SourceOnly => "Source Only",
        TableCompareStatus.TargetOnly => "Target Only",
        _ => "?"
    };

    private void LoadTableColumns(CompareObject obj)
    {
        TableCompareColumns.Clear();
        SourceCode = "";
        TargetCode = "";
        DiffModel = null;
        CanDeploy = false;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, obj.FullName, StringComparison.OrdinalIgnoreCase));

        if (tableResult == null) return;

        foreach (var col in tableResult.Columns)
            TableCompareColumns.Add(col);

        // Can deploy if source has this table and target is connected
        CanDeploy = IsTargetConnected && obj.ExistsInSource;
    }

    private async Task<Dictionary<string, string>> GetObjectsFromDatabaseAsync(string connectionString)
    {
        var objects = new Dictionary<string, string>();

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT
                    s.name as SchemaName,
                    o.name as ObjectName,
                    o.type_desc
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'V', 'TR')
                  AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var key = $"{schema}.{name}";
                objects[key] = reader.GetString(2);
            }
        }
        catch
        {
            // Ignore errors, return empty dict
        }

        return objects;
    }

    private string GetCompareStatus(bool inSource, bool inTarget)
    {
        if (inSource && inTarget) return "Both";
        if (inSource) return "Source Only";
        return "Target Only";
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterObjects();
    }

    partial void OnShowOnlyDifferencesChanged(bool value)
    {
        // In table mode, statuses are already resolved — just filter, no scan needed
        if (IsTableCompareMode)
        {
            FilterObjects();
            return;
        }

        if (value && IsSourceConnected && IsTargetConnected)
        {
            _ = ScanForDifferencesAsync();
        }
        else
        {
            FilterObjects();
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void CancelDeploy()
    {
        _deployCts?.Cancel();
    }

    private async Task ScanForDifferencesAsync()
    {
        if (!IsSourceConnected || !IsTargetConnected) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanProgress = 0;
        SourceOnlyCount = 0;
        TargetOnlyCount = 0;
        ModifiedCount = 0;
        IdenticalCount = 0;

        var objectsToScan = _allObjects.Where(o => o.ExistsInSource && o.ExistsInTarget && !o.HasBeenCompared).ToList();
        var total = objectsToScan.Count;
        var current = 0;

        // First count source-only and target-only
        SourceOnlyCount = _allObjects.Count(o => o.Status == "Source Only");
        TargetOnlyCount = _allObjects.Count(o => o.Status == "Target Only");

        // Parallel scan with bounded concurrency
        var semaphore = new SemaphoreSlim(5);

        try
        {
            var tasks = objectsToScan.Select(async obj =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();

                    obj.SourceDefinition = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
                    obj.TargetDefinition = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
                    obj.HasBeenCompared = true;

                    var sourceNorm = NormalizeForComparison(obj.SourceDefinition);
                    var targetNorm = NormalizeForComparison(obj.TargetDefinition);
                    obj.Status = sourceNorm == targetNorm ? "Identical" : "Modified";

                    var c = Interlocked.Increment(ref current);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ScanProgressText = $"Scanning {c}/{total}: {obj.ObjectName}";
                        ScanProgress = (double)c / total * 100;
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Scan cancelled ({current}/{total} objects compared)";
        }

        // Count results from whatever was compared (full or partial)
        ModifiedCount = 0;
        IdenticalCount = 0;
        foreach (var obj in _allObjects.Where(o => o.HasBeenCompared))
        {
            if (obj.Status == "Identical")
                IdenticalCount++;
            else if (obj.Status == "Modified")
                ModifiedCount++;
        }

        IsScanning = false;
        ScanProgressText = "";
        OnPropertyChanged(nameof(HasSummary));
        FilterObjects();

        if (!token.IsCancellationRequested)
            StatusMessage = $"Scan complete: {ModifiedCount} modified, {SourceOnlyCount} source only, {TargetOnlyCount} target only";
    }

    private string NormalizeForComparison(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        // Normalize line endings and trim whitespace from each line
        return string.Join("\n", code.Split('\n').Select(l => l.Trim()));
    }

    private void FilterObjects()
    {
        Objects.Clear();

        IEnumerable<CompareObject> filtered = _allObjects;

        // Apply "show only differences" filter
        if (ShowOnlyDifferences)
        {
            filtered = filtered.Where(o =>
                o.Status == "Source Only" ||
                o.Status == "Target Only" ||
                o.Status == "Modified");
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            // Normalize search text: replace underscores with spaces, then split
            var normalizedSearch = SearchText.Replace("_", " ");
            var searchTerms = normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = filtered.Where(o =>
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

        UpdateStatusMessage();
    }

    partial void OnSelectedObjectChanged(CompareObject? value)
    {
        if (value != null)
        {
            if (IsDataCompareMode)
            {
                // Auto-compare data when selecting a table in data mode
                _ = CompareDataAsync();
                return;
            }
            if (IsTableCompareMode)
            {
                LoadTableColumns(value);
                return;
            }
            _ = LoadDefinitionsAsync(value);
        }
    }

    private async Task LoadDefinitionsAsync(CompareObject obj)
    {
        SourceCode = "";
        TargetCode = "";
        Target2Code = "";
        CanDeploy = false;
        CanDeploy2 = false;

        if (IsSourceConnected && obj.ExistsInSource)
        {
            SourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
        }

        if (IsTargetConnected && obj.ExistsInTarget)
        {
            TargetCode = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
        }

        // Load Target2 definition if connected
        if (IsTarget2Connected)
        {
            Target2Code = await GetDefinitionAsync(_target2ConnectionString, obj.SchemaName, obj.ObjectName);
        }

        UpdateDiff();
        UpdateDiff2();

        // Can deploy if source has code and target is connected
        CanDeploy = IsTargetConnected && !string.IsNullOrEmpty(SourceCode);

        // Can deploy to Target2 if Target1 has code and Target2 is connected
        CanDeploy2 = IsTarget2Connected && !string.IsNullOrEmpty(TargetCode);
    }

    private async Task<string> GetDefinitionAsync(string connectionString, string schema, string objectName)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT m.definition
                FROM sys.sql_modules m
                JOIN sys.objects o ON m.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @schema AND o.name = @name";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@name", objectName);

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "(Definition not available)";
        }
        catch (Exception ex)
        {
            return $"-- Error: {ex.Message}";
        }
    }

    private void UpdateDiff()
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        DiffModel = diffBuilder.BuildDiffModel(SourceCode, TargetCode);
    }

    private void UpdateDiff2()
    {
        if (!IsTarget2Connected || string.IsNullOrEmpty(TargetCode))
        {
            DiffModel2 = null;
            return;
        }

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        DiffModel2 = diffBuilder.BuildDiffModel(TargetCode, Target2Code);
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        if (SelectedObject == null) return;

        if (IsTableCompareMode)
        {
            await DeployTableAsync(SelectedObject);
            return;
        }

        if (string.IsNullOrEmpty(SourceCode)) return;

        // Check if deploying to PROD (IP ends with .15)
        var isProd = SelectedTargetConnection?.Server.EndsWith(".15") == true;

        if (DeployRequested != null)
        {
            var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";
            var confirmed = await DeployRequested(SelectedObject.FullName, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = "Deploying...";

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so it works whether object exists or not
            var deployScript = DatabaseService.ConvertToCreateOrAlter(SourceCode);

            using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();

            StatusMessage = $"Deployed {SelectedObject.FullName} to {SelectedTargetConnection?.Server}";

            // Refresh to show updated state
            await LoadDefinitionsAsync(SelectedObject);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Deploy2Async()
    {
        if (SelectedObject == null || string.IsNullOrEmpty(TargetCode)) return;

        // Check if deploying to PROD (IP ends with .15)
        var isProd = SelectedTarget2Connection?.Server.EndsWith(".15") == true;

        if (DeployRequested != null)
        {
            var targetDesc = isProd ? "PRODUCTION" : SelectedTarget2Connection?.Server ?? "target2";
            var confirmed = await DeployRequested(SelectedObject.FullName, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = "Deploying to Target 2...";

        try
        {
            using var conn = new SqlConnection(_target2ConnectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so it works whether object exists or not
            var deployScript = DatabaseService.ConvertToCreateOrAlter(TargetCode);

            using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();

            StatusMessage = $"Deployed {SelectedObject.FullName} to {SelectedTarget2Connection?.Server}";

            // Refresh to show updated state
            await LoadDefinitionsAsync(SelectedObject);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy to Target 2 failed: {ex.Message}";
        }
    }

    private async Task DeployTableAsync(CompareObject obj, bool showStatus = true)
    {
        if (!obj.ExistsInSource) return;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, obj.FullName, StringComparison.OrdinalIgnoreCase));
        if (tableResult == null) return;

        // Confirmation for single-table deploy
        if (showStatus)
        {
            var isProd = SelectedTargetConnection?.Server.EndsWith(".15") == true;
            var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";

            if (DeployRequested != null)
            {
                var message = $"⚠️ TABLE STRUCTURE CHANGE — {obj.FullName}\n\nThis can cause data loss if columns are narrowed or removed.";
                var confirmed = await DeployRequested(message, targetDesc);
                if (!confirmed) return;
            }

            StatusMessage = $"Deploying table {obj.FullName}...";
        }

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            List<string> scripts;

            if (tableResult.Status == TableCompareStatus.SourceOnly)
            {
                scripts = [TableCompareService.GenerateCreateTableDdl(
                    tableResult.Schema, tableResult.TableName, tableResult.Columns)];
            }
            else
            {
                scripts = TableCompareService.GenerateAlterStatements(
                    tableResult.Schema, tableResult.TableName, tableResult.Columns);
            }

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var script in scripts)
                {
                    using var cmd = new SqlCommand(script, conn, transaction) { CommandTimeout = 30 };
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            if (showStatus)
            {
                StatusMessage = $"Deployed table {obj.FullName} ({scripts.Count} statement{(scripts.Count != 1 ? "s" : "")})";
                await LoadObjectsAsync();
            }
        }
        catch (Exception ex)
        {
            if (showStatus)
                StatusMessage = $"Deploy failed: {ex.Message}";
            else
                throw; // Let batch deploy catch it
        }
    }

    [RelayCommand]
    private async Task DeployColumnAsync(ColumnCompareResult col)
    {
        if (col == null || !col.IsDeployable || SelectedObject == null) return;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, SelectedObject.FullName, StringComparison.OrdinalIgnoreCase));
        if (tableResult == null) return;

        // Build scoped warning message
        var isProd = SelectedTargetConnection?.Server.EndsWith(".15") == true;
        var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";

        if (DeployRequested != null)
        {
            string message;
            if (col.Status == ColumnCompareStatus.SourceOnly)
            {
                message = $"Add column {col.ColumnName} to {SelectedObject.FullName}?\n{col.SourceType} {col.SourceNullable}";
            }
            else
            {
                var changes = new List<string>();
                if (col.SourceType != col.TargetType)
                    changes.Add($"{col.TargetType} → {col.SourceType}");
                if (col.SourceNullable != col.TargetNullable)
                    changes.Add($"{col.TargetNullable} → {col.SourceNullable}");
                if (col.SourceDefault != col.TargetDefault)
                    changes.Add($"Default: {(string.IsNullOrEmpty(col.TargetDefault) ? "(none)" : col.TargetDefault)} → {(string.IsNullOrEmpty(col.SourceDefault) ? "(none)" : col.SourceDefault)}");

                message = $"Alter column {col.ColumnName} on {SelectedObject.FullName}?\n{string.Join(", ", changes)}.\nThis may cause data truncation.";
            }

            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = $"Deploying column {col.ColumnName}...";

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            var scripts = col.Status == ColumnCompareStatus.SourceOnly
                ? TableCompareService.GenerateAlterStatements(tableResult.Schema, tableResult.TableName, [col])
                : TableCompareService.GenerateAlterStatements(tableResult.Schema, tableResult.TableName, [col]);

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var script in scripts)
                {
                    using var cmd = new SqlCommand(script, conn, transaction) { CommandTimeout = 30 };
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            // Update column status in-place (observable — UI updates automatically)
            col.Status = ColumnCompareStatus.Match;

            // Update parent table status if all columns now match
            if (tableResult.Columns.All(c => c.Status == ColumnCompareStatus.Match))
            {
                tableResult.Status = TableCompareStatus.Match;
                SelectedObject.Status = "Identical"; // setter notifies DisplayName + StatusIcon

                // Update summary counts
                ModifiedCount = _tableCompareResults.Count(t => t.Status == TableCompareStatus.Different);
                IdenticalCount = _tableCompareResults.Count(t => t.Status == TableCompareStatus.Match);
                OnPropertyChanged(nameof(HasSummary));
            }

            StatusMessage = $"Deployed column {col.ColumnName} on {SelectedObject.FullName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    // --- Data Compare Methods ---

    partial void OnIsDataCompareModeChanged(bool value)
    {
        if (value)
        {
            // Data mode needs table list — enable table mode if not already
            if (!IsTableCompareMode)
                IsTableCompareMode = true;

            // If a table is already selected, auto-compare it
            if (SelectedObject != null && IsSourceConnected && IsTargetConnected)
                _ = CompareDataAsync();
        }
        else
        {
            // Leaving data mode — clear data compare state
            DataCompareResult = null;
            DataCompareRows.Clear();
            FilteredDataRows.Clear();
            SelectedRowFields.Clear();
            DataCompareSummary = "";
        }
    }

    [RelayCommand]
    private void ToggleDataCompareMode()
    {
        IsDataCompareMode = !IsDataCompareMode;
    }

    [RelayCommand]
    private async Task CompareDataAsync()
    {
        if (SelectedObject == null ||
            string.IsNullOrEmpty(_sourceConnectionString) ||
            string.IsNullOrEmpty(_targetConnectionString))
        {
            StatusMessage = "Select a table and connect both source and target first.";
            return;
        }

        IsDataCompareMode = true;
        IsDataLoading = true;
        StatusMessage = $"Comparing data for {SelectedObject.FullName}...";
        DataCompareRows.Clear();
        FilteredDataRows.Clear();
        SelectedRowFields.Clear();
        DataCompareSummary = "";
        SelectedDataRow = null;

        try
        {
            var service = new DataCompareService();
            var parts = SelectedObject.FullName.Split('.', 2);
            var schema = parts[0];
            var table = parts.Length > 1 ? parts[1] : parts[0];

            var result = await service.CompareDataAsync(
                _sourceConnectionString, _targetConnectionString, schema, table);

            DataCompareResult = result;

            // Populate column filter dropdown
            DataFilterColumns.Clear();
            DataFilterColumns.Add(""); // "All" option
            foreach (var col in result.ColumnNames)
                DataFilterColumns.Add(col);
            DataFilterColumn = "";
            DataFilterText = "";

            // Populate rows
            foreach (var row in result.Rows)
                DataCompareRows.Add(row);

            ApplyDataFilter();
            DataCompareSummary = result.Summary;
            StatusMessage = result.Summary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Data compare failed: {ex.Message}";
            DataCompareSummary = "";
        }
        finally
        {
            IsDataLoading = false;
        }
    }

    partial void OnDataFilterColumnChanged(string value) => ApplyDataFilter();
    partial void OnDataFilterTextChanged(string value) => ApplyDataFilter();

    private void ApplyDataFilter()
    {
        FilteredDataRows.Clear();

        if (DataCompareResult == null) return;

        var filterText = DataFilterText?.Trim() ?? "";
        var filterColName = DataFilterColumn ?? "";

        int filterColIndex = -1;
        if (!string.IsNullOrEmpty(filterColName))
            filterColIndex = Array.FindIndex(DataCompareResult.ColumnNames,
                c => c.Equals(filterColName, StringComparison.OrdinalIgnoreCase));

        foreach (var row in DataCompareRows)
        {
            if (string.IsNullOrEmpty(filterText))
            {
                FilteredDataRows.Add(row);
                continue;
            }

            // Filter: check if the specified column (or any column) contains the text
            if (filterColIndex >= 0)
            {
                var val = row.GetDisplayValue(filterColIndex)?.ToString() ?? "";
                if (val.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    FilteredDataRows.Add(row);
            }
            else
            {
                // Search all columns
                var values = row.Status == DataRowStatus.TargetOnly ? row.TargetValues : row.SourceValues;
                if (values.Any(v => v?.ToString()?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true))
                    FilteredDataRows.Add(row);
            }
        }
    }

    partial void OnSelectedDataRowChanged(DataCompareRow? value)
    {
        SelectedRowFields.Clear();
        if (value == null || DataCompareResult == null) return;

        var cols = DataCompareResult.ColumnNames;
        var pkSet = new HashSet<int>(DataCompareResult.PkColumnIndices);

        for (int i = 0; i < cols.Length; i++)
        {
            var srcVal = value.SourceValues.ElementAtOrDefault(i);
            var tgtVal = value.TargetValues.ElementAtOrDefault(i);
            var isDiff = value.DifferentColumnIndices.Contains(i);

            string srcDisplay, tgtDisplay;
            bool srcNull = false, tgtNull = false;

            if (value.Status == DataRowStatus.SourceOnly)
            {
                srcDisplay = srcVal == null ? "NULL" : srcVal.ToString()!;
                tgtDisplay = "—";
                srcNull = srcVal == null;
            }
            else if (value.Status == DataRowStatus.TargetOnly)
            {
                srcDisplay = "—";
                tgtDisplay = tgtVal == null ? "NULL" : tgtVal.ToString()!;
                tgtNull = tgtVal == null;
            }
            else
            {
                srcDisplay = srcVal == null ? "NULL" : srcVal.ToString()!;
                tgtDisplay = tgtVal == null ? "NULL" : tgtVal.ToString()!;
                srcNull = srcVal == null;
                tgtNull = tgtVal == null;
            }

            SelectedRowFields.Add(new DataCompareField
            {
                ColumnName = cols[i],
                SourceDisplay = srcDisplay,
                TargetDisplay = tgtDisplay,
                IsDifferent = isDiff,
                IsPrimaryKey = pkSet.Contains(i),
                IsSourceNull = srcNull,
                IsTargetNull = tgtNull
            });
        }
    }

    [RelayCommand]
    private void ShowDataDeploySql()
    {
        if (DataCompareResult == null) return;

        var rowsToDeploy = DataCompareRows.Where(r => r.IsSelected && r.Status != DataRowStatus.Identical).ToList();
        if (rowsToDeploy.Count == 0)
        {
            StatusMessage = "Select rows to deploy first (use checkboxes).";
            return;
        }

        var sql = DataCompareService.GenerateDeploySql(DataCompareResult, rowsToDeploy);
        // Store in SourceCode so the UI can display it (reuse existing code panel)
        SourceCode = sql;
        StatusMessage = $"Generated SQL for {rowsToDeploy.Count} row(s)";
    }

    [RelayCommand]
    private async Task DeployDataRowsAsync()
    {
        if (DataCompareResult == null) return;

        var rowsToDeploy = DataCompareRows.Where(r => r.IsSelected && r.Status != DataRowStatus.Identical).ToList();
        if (rowsToDeploy.Count == 0)
        {
            StatusMessage = "Select rows to deploy first (use checkboxes).";
            return;
        }

        // Confirmation
        var isProd = SelectedTargetConnection?.Server?.EndsWith(".15") == true;
        var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";

        if (DeployRequested != null)
        {
            var message = $"Deploy {rowsToDeploy.Count} row(s) to {DataCompareResult.Schema}.{DataCompareResult.TableName} on {targetDesc}?";
            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = $"Deploying {rowsToDeploy.Count} row(s)...";

        try
        {
            var service = new DataCompareService();
            var (success, msg) = await service.DeployRowsAsync(_targetConnectionString, DataCompareResult, rowsToDeploy);

            if (success)
            {
                StatusMessage = msg;
                // Refresh data comparison
                await CompareDataAsync();
            }
            else
            {
                StatusMessage = msg;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeployFieldAsync(DataCompareField field)
    {
        if (field == null || !field.IsDifferent || DataCompareResult == null || SelectedDataRow == null)
            return;

        var colIndex = Array.FindIndex(DataCompareResult.ColumnNames,
            c => c.Equals(field.ColumnName, StringComparison.OrdinalIgnoreCase));
        if (colIndex < 0) return;

        var pkIndices = DataCompareResult.PkColumnIndices;
        var cols = DataCompareResult.ColumnNames;
        var row = SelectedDataRow;

        // Build a single UPDATE for this field
        var schema = DataCompareResult.Schema.Replace("]", "]]");
        var table = DataCompareResult.TableName.Replace("]", "]]");
        var tableRef = $"[{schema}].[{table}]";

        var isProd = SelectedTargetConnection?.Server?.EndsWith(".15") == true;
        var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";

        if (DeployRequested != null)
        {
            var srcVal = row.SourceValues[colIndex]?.ToString() ?? "NULL";
            var tgtVal = row.TargetValues[colIndex]?.ToString() ?? "NULL";
            var message = $"Update {field.ColumnName} from '{tgtVal}' to '{srcVal}' on {DataCompareResult.Schema}.{DataCompareResult.TableName}?";
            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            var setClauses = $"[{cols[colIndex]}] = @setVal";
            var whereClauses = pkIndices.Select(i => $"[{cols[i]}] = @pk_{i}");
            var sql = $"UPDATE {tableRef} SET {setClauses} WHERE {string.Join(" AND ", whereClauses)}";

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@setVal", row.SourceValues[colIndex] ?? DBNull.Value);
            foreach (var i in pkIndices)
                cmd.Parameters.AddWithValue($"@pk_{i}", row.SourceValues[i] ?? DBNull.Value);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 1)
            {
                StatusMessage = $"Deployed {field.ColumnName}";
                await CompareDataAsync();
            }
            else
            {
                StatusMessage = $"UPDATE affected {affected} rows (expected 1)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAllDataRows()
    {
        foreach (var row in FilteredDataRows)
            if (row.Status != DataRowStatus.Identical)
                row.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllDataRows()
    {
        foreach (var row in DataCompareRows)
            row.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleTableCompareMode()
    {
        IsTableCompareMode = !IsTableCompareMode;
    }

    [RelayCommand]
    private void ToggleTarget2()
    {
        ShowTarget2 = !ShowTarget2;
        if (!ShowTarget2)
        {
            // Clear Target2 connection when hiding
            SelectedTarget2Connection = null;
            IsTarget2Connected = false;
            Target2Status = "Not connected";
            Target2Code = "";
            DiffModel2 = null;
            CanDeploy2 = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Connect if we have selections but aren't connected yet
        if (SelectedSourceConnection != null && !IsSourceConnected)
        {
            await ConnectSourceAsync(SelectedSourceConnection);
        }
        if (SelectedTargetConnection != null && !IsTargetConnected)
        {
            await ConnectTargetAsync(SelectedTargetConnection);
        }

        await LoadObjectsAsync();
        UpdateStatusMessage();
    }

    [RelayCommand]
    private async Task CopySourceAsync()
    {
        if (!string.IsNullOrEmpty(SourceCode))
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(SourceCode);
                StatusMessage = "Source definition copied to clipboard";
            }
        }
    }

    [RelayCommand]
    private async Task CopyTargetAsync()
    {
        if (!string.IsNullOrEmpty(TargetCode))
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(TargetCode);
                StatusMessage = "Target definition copied to clipboard";
            }
        }
    }

    private void UpdateStatusMessage()
    {
        var showing = Objects.Count;
        var total = _allObjects.Count;
        if (showing == total)
            StatusMessage = $"Showing {total} objects";
        else
            StatusMessage = $"Showing {showing} of {total} objects";
    }

    public void UpdateSelectedCount()
    {
        SelectedCount = Objects.Count(o => o.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var obj in Objects.Where(o => o.ExistsInSource))
        {
            obj.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var obj in Objects)
        {
            obj.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task DeploySelectedAsync()
    {
        var selectedObjects = Objects.Where(o => o.IsSelected && o.ExistsInSource).ToList();
        if (selectedObjects.Count == 0) return;

        // Check if deploying to PROD
        var isProd = SelectedTargetConnection?.Server.EndsWith(".15") == true;
        var targetDesc = isProd ? "PRODUCTION" : SelectedTargetConnection?.Server ?? "target";

        if (DeployRequested != null)
        {
            var label = IsTableCompareMode ? "tables" : "objects";
            var objectNames = string.Join(", ", selectedObjects.Take(3).Select(o => o.ObjectName));
            if (selectedObjects.Count > 3) objectNames += $" (+{selectedObjects.Count - 3} more)";

            var message = IsTableCompareMode
                ? $"⚠️ TABLE STRUCTURE CHANGE — {selectedObjects.Count} {label}: {objectNames}\n\nThis can cause data loss if columns are narrowed or removed."
                : $"{selectedObjects.Count} {label}: {objectNames}";

            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        _deployCts?.Cancel();
        _deployCts?.Dispose();
        _deployCts = new CancellationTokenSource();
        var token = _deployCts.Token;

        IsDeploying = true;
        var total = selectedObjects.Count;
        var successCount = 0;
        var failCount = 0;

        try
        {
            for (int i = 0; i < selectedObjects.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var obj = selectedObjects[i];
                StatusMessage = $"Deploying {i + 1}/{total}: {obj.ObjectName}...";

                try
                {
                    if (IsTableCompareMode)
                    {
                        await DeployTableAsync(obj, showStatus: false);
                    }
                    else
                    {
                        // Get definition if not cached
                        var sourceCode = obj.SourceDefinition;
                        if (string.IsNullOrEmpty(sourceCode))
                        {
                            sourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
                        }

                        var deployScript = DatabaseService.ConvertToCreateOrAlter(sourceCode);

                        using var conn = new SqlConnection(_targetConnectionString);
                        await conn.OpenAsync(token);
                        using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
                        await cmd.ExecuteNonQueryAsync(token);
                    }

                    obj.IsSelected = false;
                    successCount++;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    failCount++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Deploy cancelled — {successCount} succeeded, {failCount} failed, {total - successCount - failCount} skipped";
        }

        IsDeploying = false;
        UpdateSelectedCount();

        if (!token.IsCancellationRequested)
        {
            if (failCount == 0)
                StatusMessage = $"Successfully deployed {successCount} {(IsTableCompareMode ? "tables" : "objects")} to {targetDesc}";
            else
                StatusMessage = $"Deployed {successCount}, {failCount} failed";
        }

        // Refresh to update states
        await LoadObjectsAsync();
    }

    public void AddConnection(SavedConnection conn, string? password, bool isSource)
    {
        // Store password if provided (for SQL auth)
        if (!conn.UseWindowsAuth && !string.IsNullOrEmpty(password))
        {
            var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
            _passwords[key] = password;
        }

        // Save to settings for future use
        _settings.AddRecentConnection(conn);

        // Add to all dropdowns if not already present
        if (!SourceConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            SourceConnections.Insert(0, conn);
        }
        if (!TargetConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            TargetConnections.Insert(0, conn);
        }
        if (!Target2Connections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            Target2Connections.Insert(0, conn);
        }

        // Select it for the appropriate side
        if (isSource)
        {
            SelectedSourceConnection = conn;
        }
        else
        {
            SelectedTargetConnection = conn;
        }
    }

    public void AddConnectionToTarget2(SavedConnection conn, string? password)
    {
        // Store password if provided (for SQL auth)
        if (!conn.UseWindowsAuth && !string.IsNullOrEmpty(password))
        {
            var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
            _passwords[key] = password;
        }

        // Save to settings for future use
        _settings.AddRecentConnection(conn);

        // Add to all dropdowns if not already present
        if (!SourceConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            SourceConnections.Insert(0, conn);
        }
        if (!TargetConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            TargetConnections.Insert(0, conn);
        }
        if (!Target2Connections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            Target2Connections.Insert(0, conn);
        }

        // Select it for Target2
        SelectedTarget2Connection = conn;
    }
}

public class CompareObject : ObservableObject
{
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string FullName { get; set; } = "";
    public bool ExistsInSource { get; set; }
    public bool ExistsInTarget { get; set; }

    private string _status = "";
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(StatusIcon));
            }
        }
    }

    // Cached definitions for comparison
    public string? SourceDefinition { get; set; }
    public string? TargetDefinition { get; set; }
    public bool HasBeenCompared { get; set; }

    // Selection for batch deploy
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayName => $"{FullName} [{Status}]";
    public string StatusIcon => Status switch
    {
        "Both" => "=",
        "Identical" => "=",
        "Modified" => "~",
        "Source Only" => "+",
        "Target Only" => "-",
        _ => "?"
    };
}
