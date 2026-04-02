using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class CompareViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ConnectionRegistry? _registry;
    private List<CompareObject> _allObjects = new();

    // Table structure comparison
    private List<TableCompareResult> _tableCompareResults = new();

    // Scan/deploy cancellation
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _deployCts;

    // Store passwords temporarily for non-Windows auth (not persisted to disk)
    // Legacy — kept for backward compat when registry is not available
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

    public string ToggleTarget2ButtonText => ShowTarget2 ? "- Target 2" : "+ Target 2";

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
    public bool HasObjects => Objects.Count > 0;

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

    public CompareViewModel(SettingsService settings, ConnectionRegistry? registry = null)
    {
        _settings = settings;
        _registry = registry;
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
        var connections = _registry != null
            ? _registry.Connections.Select(m => m.Config).ToList()
            : _settings.Settings.RecentConnections;

        foreach (var conn in connections)
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

        if (string.IsNullOrEmpty(_sourceConnectionString))
        {
            IsSourceConnected = false;
            SourceStatus = "Password required — click Connect to retry";
            return;
        }

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
            SourceStatus = $"Failed: {_lastConnectionError ?? "Connection failed"}";
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

        if (string.IsNullOrEmpty(_targetConnectionString))
        {
            IsTargetConnected = false;
            TargetStatus = "Password required — click Connect to retry";
            return;
        }

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
            TargetStatus = $"Failed: {_lastConnectionError ?? "Connection failed"}";
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

        if (string.IsNullOrEmpty(_target2ConnectionString))
        {
            IsTarget2Connected = false;
            Target2Status = "Password required — click Connect to retry";
            return;
        }

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
            Target2Status = $"Failed: {_lastConnectionError ?? "Connection failed"}";
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
        // Try registry first — it has already resolved connection strings
        if (_registry != null)
        {
            var managed = _registry.GetById(conn.Id);
            if (managed?.ResolvedConnectionString != null)
                return managed.ResolvedConnectionString;
        }

        var settings = new ConnectionSettings
        {
            Server = conn.Server,
            Database = conn.Database,
            UseWindowsAuth = conn.UseWindowsAuth,
            TrustServerCertificate = conn.TrustServerCertificate
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
                    return ""; // No password available — caller should prompt
            }
        }

        return settings.ConnectionString;
    }

    private bool IsProductionConnection(SavedConnection? conn)
    {
        if (conn == null) return false;
        // Use environment classification from registry if available
        if (_registry != null)
        {
            var managed = _registry.GetById(conn.Id);
            if (managed != null)
                return managed.IsProduction;
        }
        // Fallback: check environment field on the connection itself
        if (conn.Environment == "Production") return true;
        // Legacy fallback: IP heuristic
        return conn.Server.EndsWith(".15");
    }

    private string GetTargetDescription(SavedConnection? conn)
        => IsProductionConnection(conn) ? "PRODUCTION" : conn?.Server ?? "target";

    private string? _lastConnectionError;

    private async Task<bool> TestConnectionAsync(string connectionString)
    {
        try
        {
            _lastConnectionError = null;
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _lastConnectionError = ex.Message;
            return false;
        }
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
        "Uncompared" or "Both" => "?",
        "Identical" => "=",
        "Modified" => "~",
        "Source Only" => "+",
        "Target Only" => "-",
        _ => "?"
    };
}
