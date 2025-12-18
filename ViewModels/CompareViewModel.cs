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

    // Show only differences feature
    [ObservableProperty]
    private bool _showOnlyDifferences;

    [ObservableProperty]
    private bool _isScanning;

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
        if (conn.UseWindowsAuth)
        {
            return $"Server={conn.Server};Database={conn.Database};Integrated Security=True;TrustServerCertificate=True;";
        }

        // For SQL auth, check global PasswordStore first (from initial login)
        var globalPassword = PasswordStore.Get(conn.Server, conn.Database, conn.Username);
        if (!string.IsNullOrEmpty(globalPassword))
        {
            return $"Server={conn.Server};Database={conn.Database};User Id={conn.Username};Password={globalPassword};TrustServerCertificate=True;";
        }

        // Then check local passwords (from QuickConnectionDialog)
        var key = $"{conn.Server}|{conn.Database}|{conn.Username}";
        if (_passwords.TryGetValue(key, out var password))
        {
            return $"Server={conn.Server};Database={conn.Database};User Id={conn.Username};Password={password};TrustServerCertificate=True;";
        }

        // No password available - connection will likely fail
        return $"Server={conn.Server};Database={conn.Database};Integrated Security=True;TrustServerCertificate=True;";
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

        var sourceObjects = new Dictionary<string, string>();
        var targetObjects = new Dictionary<string, string>();

        if (IsSourceConnected)
        {
            sourceObjects = await GetObjectsFromDatabaseAsync(_sourceConnectionString);
        }

        if (IsTargetConnected)
        {
            targetObjects = await GetObjectsFromDatabaseAsync(_targetConnectionString);
        }

        // Merge objects from both databases
        var allKeys = sourceObjects.Keys.Union(targetObjects.Keys).OrderBy(k => k);
        _allObjects.Clear();

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

        FilterObjects();

        // Re-select the same object (new instance) and reload definitions
        if (!string.IsNullOrEmpty(selectedFullName))
        {
            var matchingObject = Objects.FirstOrDefault(o => o.FullName == selectedFullName);
            if (matchingObject != null)
            {
                SelectedObject = matchingObject;
                await LoadDefinitionsAsync(matchingObject);
            }
            else
            {
                SelectedObject = null;
            }
        }
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
        if (value && IsSourceConnected && IsTargetConnected)
        {
            _ = ScanForDifferencesAsync();
        }
        else
        {
            FilterObjects();
        }
    }

    private async Task ScanForDifferencesAsync()
    {
        if (!IsSourceConnected || !IsTargetConnected) return;

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

        foreach (var obj in objectsToScan)
        {
            current++;
            ScanProgressText = $"Scanning {current}/{total}: {obj.ObjectName}";
            ScanProgress = (double)current / total * 100;

            // Fetch definitions
            obj.SourceDefinition = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
            obj.TargetDefinition = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
            obj.HasBeenCompared = true;

            // Compare - normalize whitespace for comparison
            var sourceNorm = NormalizeForComparison(obj.SourceDefinition);
            var targetNorm = NormalizeForComparison(obj.TargetDefinition);

            if (sourceNorm == targetNorm)
            {
                obj.Status = "Identical";
                IdenticalCount++;
            }
            else
            {
                obj.Status = "Modified";
                ModifiedCount++;
            }
        }

        // Count already-compared objects
        foreach (var obj in _allObjects.Where(o => o.HasBeenCompared))
        {
            if (obj.Status == "Identical" && !objectsToScan.Contains(obj))
                IdenticalCount++;
            else if (obj.Status == "Modified" && !objectsToScan.Contains(obj))
                ModifiedCount++;
        }

        IsScanning = false;
        ScanProgressText = "";
        OnPropertyChanged(nameof(HasSummary));
        FilterObjects();
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
        if (SelectedObject == null || string.IsNullOrEmpty(SourceCode)) return;

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
            var deployScript = ConvertToCreateOrAlter(SourceCode);

            using var cmd = new SqlCommand(deployScript, conn);
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
            var deployScript = ConvertToCreateOrAlter(TargetCode);

            using var cmd = new SqlCommand(deployScript, conn);
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

    /// <summary>
    /// Converts CREATE PROCEDURE/FUNCTION/VIEW/TRIGGER to CREATE OR ALTER
    /// so deploy works whether object exists or not (SQL Server 2016+)
    /// </summary>
    private static string ConvertToCreateOrAlter(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return definition;

        // Skip if already has "OR ALTER"
        if (System.Text.RegularExpressions.Regex.IsMatch(definition, @"CREATE\s+OR\s+ALTER",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return definition;
        }

        // Pattern matches CREATE PROCEDURE/PROC/FUNCTION/VIEW/TRIGGER anywhere in the string
        // More flexible to handle leading whitespace, comments, etc.
        var pattern = @"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b";
        var replacement = "CREATE OR ALTER $1";

        return System.Text.RegularExpressions.Regex.Replace(
            definition,
            pattern,
            replacement,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            var objectNames = string.Join(", ", selectedObjects.Take(3).Select(o => o.ObjectName));
            if (selectedObjects.Count > 3) objectNames += $" (+{selectedObjects.Count - 3} more)";

            var confirmed = await DeployRequested($"{selectedObjects.Count} objects: {objectNames}", targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = $"Deploying {selectedObjects.Count} objects...";
        var successCount = 0;
        var failCount = 0;

        foreach (var obj in selectedObjects)
        {
            try
            {
                // Get definition if not cached
                var sourceCode = obj.SourceDefinition;
                if (string.IsNullOrEmpty(sourceCode))
                {
                    sourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
                }

                // Convert CREATE to CREATE OR ALTER so it works whether object exists or not
                var deployScript = ConvertToCreateOrAlter(sourceCode);

                using var conn = new SqlConnection(_targetConnectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(deployScript, conn);
                await cmd.ExecuteNonQueryAsync();

                obj.IsSelected = false;
                successCount++;
            }
            catch
            {
                failCount++;
            }
        }

        UpdateSelectedCount();

        if (failCount == 0)
            StatusMessage = $"Successfully deployed {successCount} objects to {targetDesc}";
        else
            StatusMessage = $"Deployed {successCount} objects, {failCount} failed";

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
    public string Status { get; set; } = "";

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
