using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ConnectionViewModel : ViewModelBase
{
    private readonly DatabaseService _db;
    private readonly SettingsService _settings;
    private readonly Action<ConnectionSettings> _onConnected;

    [ObservableProperty]
    private string _server = "";

    [ObservableProperty]
    private string _database = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _useWindowsAuth;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private ObservableCollection<SavedConnection> _recentConnections = new();

    [ObservableProperty]
    private SavedConnection? _selectedConnection;

    [ObservableProperty]
    private bool _useRecentConnection;

    public bool HasRecentConnections => RecentConnections.Count > 0;

    // Show password field when using recent connection with SQL auth
    public bool NeedsPassword => UseRecentConnection && SelectedConnection != null && !SelectedConnection.UseWindowsAuth;

    public ConnectionViewModel(DatabaseService db, SettingsService settings, Action<ConnectionSettings> onConnected)
    {
        _db = db;
        _settings = settings;
        _onConnected = onConnected;

        // Load recent connections
        foreach (var conn in _settings.Settings.RecentConnections)
        {
            RecentConnections.Add(conn);
        }

        // If there are recent connections, default to that mode and select the first one
        if (RecentConnections.Count > 0)
        {
            UseRecentConnection = true;
            SelectedConnection = RecentConnections[0];
        }
    }

    partial void OnSelectedConnectionChanged(SavedConnection? value)
    {
        if (value != null)
        {
            Server = value.Server;
            Database = value.Database;
            Username = value.Username;
            UseWindowsAuth = value.UseWindowsAuth;
            Password = ""; // Don't store passwords
        }
        OnPropertyChanged(nameof(NeedsPassword));
    }

    partial void OnUseRecentConnectionChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsPassword));
        // Clear password when switching modes
        Password = "";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        // Validation depends on mode
        if (UseRecentConnection)
        {
            if (SelectedConnection == null)
            {
                ErrorMessage = "Please select a connection.";
                return;
            }
            if (!SelectedConnection.UseWindowsAuth && string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Please enter a password.";
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Server))
            {
                ErrorMessage = "Please enter a server address.";
                return;
            }
            if (string.IsNullOrWhiteSpace(Database))
            {
                ErrorMessage = "Please enter a database name.";
                return;
            }
            if (!UseWindowsAuth && string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Please enter a password.";
                return;
            }
        }

        IsConnecting = true;
        ErrorMessage = "";

        var settings = new ConnectionSettings
        {
            Server = UseRecentConnection ? SelectedConnection!.Server : Server,
            Database = UseRecentConnection ? SelectedConnection!.Database : Database,
            Username = UseRecentConnection ? SelectedConnection!.Username : Username,
            Password = Password,
            UseWindowsAuth = UseRecentConnection ? SelectedConnection!.UseWindowsAuth : UseWindowsAuth
        };

        _db.SetConnection(settings);
        var success = await _db.TestConnectionAsync();

        IsConnecting = false;

        if (success)
        {
            // Store password in memory for reuse in Compare tab (SQL auth only)
            if (!settings.UseWindowsAuth && !string.IsNullOrEmpty(settings.Password))
            {
                PasswordStore.Store(settings.Server, settings.Database, settings.Username, settings.Password);
            }

            // Save to recent connections (moves to top if already exists)
            _settings.AddRecentConnection(new SavedConnection
            {
                Server = settings.Server,
                Database = settings.Database,
                Username = settings.Username,
                UseWindowsAuth = settings.UseWindowsAuth
            });
            _onConnected(settings);
        }
        else
        {
            ErrorMessage = "Could not connect to server. Check your credentials.";
        }
    }
}
