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

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _canDeploy;

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
        RestoreLastComparison();
    }

    private void LoadSavedConnections()
    {
        foreach (var conn in _settings.Settings.RecentConnections)
        {
            SourceConnections.Add(conn);
            TargetConnections.Add(conn);
        }
    }

    private void RestoreLastComparison()
    {
        var (lastSource, lastTarget) = _settings.GetLastComparison();

        if (lastSource != null)
        {
            var source = SourceConnections.FirstOrDefault(c =>
                c.Server == lastSource.Server && c.Database == lastSource.Database);
            if (source != null)
            {
                SelectedSourceConnection = source;
            }
        }

        if (lastTarget != null)
        {
            var target = TargetConnections.FirstOrDefault(c =>
                c.Server == lastTarget.Server && c.Database == lastTarget.Database);
            if (target != null)
            {
                SelectedTargetConnection = target;
            }
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
            await LoadObjectsAsync();
        }
        else
        {
            IsTargetConnected = false;
            TargetStatus = "Connection failed";
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
            var searchTerms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
        CanDeploy = false;

        if (IsSourceConnected && obj.ExistsInSource)
        {
            SourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
        }

        if (IsTargetConnected && obj.ExistsInTarget)
        {
            TargetCode = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
        }

        UpdateDiff();

        // Can deploy if source has code and target is connected
        CanDeploy = IsTargetConnected && !string.IsNullOrEmpty(SourceCode);
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

            // Drop existing if exists, then create
            var dropSql = $@"
                IF OBJECT_ID('{SelectedObject.SchemaName}.{SelectedObject.ObjectName}') IS NOT NULL
                    DROP PROCEDURE [{SelectedObject.SchemaName}].[{SelectedObject.ObjectName}]";

            // For simplicity, we'll execute the source definition directly
            // This assumes the definition is a CREATE statement
            // You may need to convert CREATE to ALTER or handle differently

            using var cmd = new SqlCommand(SourceCode, conn);
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
    private async Task RefreshAsync()
    {
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

                using var conn = new SqlConnection(_targetConnectionString);
                await conn.OpenAsync();

                using var cmd = new SqlCommand(sourceCode, conn);
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

        // Add to both dropdowns if not already present
        if (!SourceConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            SourceConnections.Insert(0, conn);
        }
        if (!TargetConnections.Any(c => c.Server == conn.Server && c.Database == conn.Database))
        {
            TargetConnections.Insert(0, conn);
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
