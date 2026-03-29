using System.Collections.ObjectModel;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class ManagedConnection : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public SavedConnection Config { get; set; }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public string? ResolvedConnectionString { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public List<string>? Databases { get; set; }

    // Convenience
    public string Id => Config.Id;
    public string DisplayName => Config.DisplayName;
    public string? Color => Config.Color;
    public string Environment => Config.Environment;
    public bool IsProduction => Config.Environment == "Production";

    public ManagedConnection(SavedConnection config)
    {
        Config = config;
    }
}

public class ConnectionRegistry
{
    private readonly SettingsService _settings;

    public ObservableCollection<ManagedConnection> Connections { get; } = new();

    public IEnumerable<ManagedConnection> ActiveConnections
        => Connections.Where(c => c.IsConnected);

    // Events
    public event Action<ManagedConnection>? ConnectionAdded;
    public event Action<ManagedConnection>? ConnectionRemoved;
    public event Action<ManagedConnection>? ConnectionStateChanged;

    /// <summary>
    /// View subscribes to this to prompt user for a password when needed.
    /// Returns the password, or null if the user cancelled.
    /// </summary>
    public event Func<SavedConnection, Task<string?>>? PasswordRequested;

    public ConnectionRegistry(SettingsService settings)
    {
        _settings = settings;
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    public void Load()
    {
        Connections.Clear();
        var migrated = false;

        foreach (var saved in _settings.Settings.RecentConnections)
        {
            // Migration: generate Id for legacy entries
            if (string.IsNullOrEmpty(saved.Id) || saved.Id == Guid.Empty.ToString())
            {
                saved.Id = Guid.NewGuid().ToString();
                migrated = true;
            }

            // Migration: default Environment for legacy entries
            if (string.IsNullOrEmpty(saved.Environment))
            {
                saved.Environment = "Unknown";
                migrated = true;
            }

            Connections.Add(new ManagedConnection(saved));
        }

        if (migrated)
            Save();
    }

    public void Save()
    {
        _settings.Settings.RecentConnections = Connections.Select(c => c.Config).ToList();
        _settings.Save();
    }

    // ── Connect / Disconnect ─────────────────────────────────────────

    public async Task<(bool Success, string? Error)> ConnectAsync(string connectionId)
    {
        var managed = GetById(connectionId);
        if (managed == null)
            return (false, "Connection not found");

        var connStr = await ResolveConnectionStringAsync(managed.Config);
        if (connStr == null)
            return (false, "Password required but not provided");

        try
        {
            // Test the connection
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // Cache databases while we have a live connection
            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            managed.ResolvedConnectionString = connStr;
            managed.ConnectedAt = DateTime.Now;
            managed.Databases = databases;
            managed.IsConnected = true;

            ConnectionStateChanged?.Invoke(managed);
            return (true, null);
        }
        catch (Exception ex)
        {
            managed.IsConnected = false;
            managed.ResolvedConnectionString = null;
            return (false, ex.Message);
        }
    }

    public void Disconnect(string connectionId)
    {
        var managed = GetById(connectionId);
        if (managed == null) return;

        // Clear connection pool for this connection string
        if (managed.ResolvedConnectionString != null)
        {
            try
            {
                SqlConnection.ClearPool(new SqlConnection(managed.ResolvedConnectionString));
            }
            catch { /* pool clear is best-effort */ }
        }

        managed.IsConnected = false;
        managed.ResolvedConnectionString = null;
        managed.ConnectedAt = null;
        managed.Databases = null;

        ConnectionStateChanged?.Invoke(managed);
    }

    // ── Connection String Resolution ─────────────────────────────────

    public string? GetConnectionString(string connectionId)
    {
        return GetById(connectionId)?.ResolvedConnectionString;
    }

    private async Task<string?> ResolveConnectionStringAsync(SavedConnection config)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = config.Server,
            InitialCatalog = config.Database,
            TrustServerCertificate = config.TrustServerCertificate,
            ConnectTimeout = 5,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 10
        };

        if (config.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = config.Username;

            // Try PasswordStore first
            var password = PasswordStore.Get(config.Server, config.Database, config.Username);

            // Not found — prompt via event
            if (string.IsNullOrEmpty(password))
            {
                if (PasswordRequested != null)
                {
                    password = await PasswordRequested.Invoke(config);
                }

                if (string.IsNullOrEmpty(password))
                    return null;

                // Store for future use
                PasswordStore.Store(config.Server, config.Database, config.Username, password);
                PasswordStore.Save();
            }

            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    // ── CRUD ─────────────────────────────────────────────────────────

    public ManagedConnection Add(SavedConnection config)
    {
        // Ensure Id
        if (string.IsNullOrEmpty(config.Id))
            config.Id = Guid.NewGuid().ToString();

        // Default environment
        if (string.IsNullOrEmpty(config.Environment))
            config.Environment = "Unknown";

        var managed = new ManagedConnection(config);
        Connections.Add(managed);
        Save();

        ConnectionAdded?.Invoke(managed);
        return managed;
    }

    public void Remove(string connectionId)
    {
        var managed = GetById(connectionId);
        if (managed == null) return;

        if (managed.IsConnected)
            Disconnect(connectionId);

        Connections.Remove(managed);
        Save();

        ConnectionRemoved?.Invoke(managed);
    }

    public void Update(string connectionId, SavedConnection config)
    {
        var managed = GetById(connectionId);
        if (managed == null) return;

        // Preserve the Id
        config.Id = connectionId;
        managed.Config = config;
        Save();
    }

    // ── Lookup ───────────────────────────────────────────────────────

    public ManagedConnection? GetById(string connectionId)
        => Connections.FirstOrDefault(c => c.Id == connectionId);

    public ManagedConnection? FindByServerAndDatabase(string server, string database, string? username = null)
        => Connections.FirstOrDefault(c =>
            c.Config.Server == server &&
            c.Config.Database == database &&
            (username == null || c.Config.Username == username));
}
