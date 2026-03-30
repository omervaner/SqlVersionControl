using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ConnectionManagerViewModel : ViewModelBase
{
    private readonly ConnectionRegistry _registry;
    private readonly DatabaseService _db;

    // ── Left panel: connection list ──────────────────────────────────

    public ObservableCollection<ManagedConnection> Connections => _registry.Connections;

    [ObservableProperty]
    private ManagedConnection? _selectedConnection;

    [ObservableProperty]
    private string _searchText = "";

    // ── Right panel: edit form ───────────────────────────────────────

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editServer = "";

    [ObservableProperty]
    private string _editDatabase = "";

    [ObservableProperty]
    private string _editUsername = "";

    [ObservableProperty]
    private string _editPassword = "";

    [ObservableProperty]
    private bool _editUseWindowsAuth;

    [ObservableProperty]
    private string _editEnvironment = "Unknown";

    [ObservableProperty]
    private bool _editTrustServerCertificate = true;

    [ObservableProperty]
    private string _editColor = "#88a1bb";

    [ObservableProperty]
    private bool _editConnectOnStartup;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNewConnection;

    [ObservableProperty]
    private string _testResult = "";

    [ObservableProperty]
    private bool _testSuccess;

    [ObservableProperty]
    private bool _isTesting;

    // Computed enable/disable for Connect and Disconnect buttons
    public bool CanConnect => SelectedConnection == null || !SelectedConnection.IsConnected;
    public bool CanDisconnect => SelectedConnection != null && SelectedConnection.IsConnected;

    private void RefreshButtonStates()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public static readonly string[] Environments =
        ["Dev", "Test", "Staging", "Production", "Other", "Unknown"];

    public static readonly (string Hex, string Label)[] PresetColors =
    [
        ("#e74c3c", "Production"),
        ("#e9c880", "UAT/Staging"),
        ("#b7bd73", "Dev/Test"),
        ("#88a1bb", "Other"),
        ("#ad95b8", "Purple"),
        ("#6bb5c9", "Teal"),
    ];

    public event Func<string, Task<bool>>? ConfirmRequested;

    public ConnectionManagerViewModel(ConnectionRegistry registry, DatabaseService db)
    {
        _registry = registry;
        _db = db;
    }

    // ── Selection ────────────────────────────────────────────────────

    partial void OnSelectedConnectionChanged(ManagedConnection? value)
    {
        if (value == null)
        {
            IsEditing = false;
            return;
        }

        LoadFormFromConnection(value.Config);
        IsEditing = true;
        IsNewConnection = false;
        TestResult = "";
        RefreshButtonStates();
    }

    private void LoadFormFromConnection(SavedConnection config)
    {
        EditName = config.Name ?? "";
        EditServer = config.Server;
        EditDatabase = config.Database;
        EditUsername = config.Username;
        EditPassword = PasswordStore.Get(config.Server, config.Database, config.Username) ?? "";
        EditUseWindowsAuth = config.UseWindowsAuth;
        EditEnvironment = config.Environment;
        EditTrustServerCertificate = config.TrustServerCertificate;
        EditColor = config.Color ?? "#88a1bb";
        EditConnectOnStartup = config.ConnectOnStartup;
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void AddNew()
    {
        SelectedConnection = null;
        EditName = "";
        EditServer = "";
        EditDatabase = "";
        EditUsername = "";
        EditPassword = "";
        EditUseWindowsAuth = false;
        EditEnvironment = "Unknown";
        EditTrustServerCertificate = true;
        EditColor = "#88a1bb";
        EditConnectOnStartup = false;
        TestResult = "";

        IsEditing = true;
        IsNewConnection = true;
        RefreshButtonStates();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedConnection == null) return;

        if (ConfirmRequested != null)
        {
            var confirmed = await ConfirmRequested($"Delete connection '{SelectedConnection.DisplayName}'?");
            if (!confirmed) return;
        }

        _registry.Remove(SelectedConnection.Id);
        SelectedConnection = Connections.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            TestResult = "Connection name is required.";
            TestSuccess = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(EditServer))
        {
            TestResult = "Server is required.";
            TestSuccess = false;
            return;
        }

        var config = new SavedConnection
        {
            Name = EditName.Trim(),
            Server = EditServer.Trim(),
            Database = EditDatabase.Trim(),
            Username = EditUsername.Trim(),
            UseWindowsAuth = EditUseWindowsAuth,
            Environment = EditEnvironment,
            TrustServerCertificate = EditTrustServerCertificate,
            Color = EditColor,
            ConnectOnStartup = EditConnectOnStartup,
        };

        // Store password if SQL auth
        if (!config.UseWindowsAuth && !string.IsNullOrEmpty(EditPassword))
        {
            PasswordStore.Store(config.Server, config.Database, config.Username, EditPassword);
            PasswordStore.Save();
        }

        if (IsNewConnection)
        {
            var managed = _registry.Add(config);
            SelectedConnection = managed;
            IsNewConnection = false;
        }
        else if (SelectedConnection != null)
        {
            config.Id = SelectedConnection.Id;
            config.SortOrder = SelectedConnection.Config.SortOrder;
            _registry.Update(SelectedConnection.Id, config);
            // Refresh the form with saved values
            LoadFormFromConnection(config);
        }

        TestResult = "Saved.";
        TestSuccess = true;
        RefreshButtonStates();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        if (IsNewConnection)
        {
            IsEditing = false;
            IsNewConnection = false;
        }
        else if (SelectedConnection != null)
        {
            // Revert to saved values
            LoadFormFromConnection(SelectedConnection.Config);
        }
        TestResult = "";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResult = "";

        try
        {
            var connStr = BuildTestConnectionString();
            if (connStr == null)
            {
                TestResult = "Password is required for SQL auth.";
                TestSuccess = false;
                return;
            }

            var success = await _db.TestConnectionAsync(connStr);
            TestResult = success ? "Connection successful!" : "Connection failed.";
            TestSuccess = success;
        }
        catch (Exception ex)
        {
            TestResult = $"Error: {ex.Message}";
            TestSuccess = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private string? BuildTestConnectionString()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = EditServer.Trim(),
            InitialCatalog = EditDatabase.Trim(),
            TrustServerCertificate = EditTrustServerCertificate,
            ConnectTimeout = 5,
        };

        if (EditUseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = EditUsername.Trim();

            var password = !string.IsNullOrEmpty(EditPassword)
                ? EditPassword
                : PasswordStore.Get(EditServer.Trim(), EditDatabase.Trim(), EditUsername.Trim());

            if (string.IsNullOrEmpty(password))
                return null;

            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        // Auto-save if new or unsaved
        if (IsNewConnection || SelectedConnection == null)
        {
            Save();
            if (SelectedConnection == null) return; // Save failed (validation)
        }

        var (success, error) = await _registry.ConnectAsync(SelectedConnection.Id);
        if (success)
        {
            TestResult = "Connected!";
            TestSuccess = true;
        }
        else
        {
            TestResult = $"Failed: {error}";
            TestSuccess = false;
        }
        RefreshButtonStates();
    }

    [RelayCommand]
    private void Disconnect()
    {
        if (SelectedConnection == null) return;
        _registry.Disconnect(SelectedConnection.Id);
        TestResult = "Disconnected.";
        TestSuccess = false;
        RefreshButtonStates();
    }
}
