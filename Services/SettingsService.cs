using System.Text.Json;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class SettingsService
{
    public static readonly string DefaultDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lookout");

    private static readonly string SettingsPath = Path.Combine(DefaultDataFolder, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        MigrateLegacyDataFolder();
        Load();
        PasswordStore.Load();
    }

    /// <summary>
    /// One-time migration: move data from old "SqlVersionControl" folder to new "Lookout" folder.
    /// </summary>
    private static void MigrateLegacyDataFolder()
    {
        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlVersionControl");

        if (Directory.Exists(legacyFolder) && !Directory.Exists(DefaultDataFolder))
        {
            try
            {
                Directory.Move(legacyFolder, DefaultDataFolder);
            }
            catch
            {
                // If move fails (permissions, etc.), just start fresh
            }
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });

            // Atomic write: write to temp file, back up existing, then rename
            var tempPath = SettingsPath + ".tmp";
            var backupPath = SettingsPath + ".bak";

            File.WriteAllText(tempPath, json);

            if (File.Exists(SettingsPath))
                File.Copy(SettingsPath, backupPath, overwrite: true);

            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SettingsService.Save", ex);
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else if (File.Exists(SettingsPath + ".bak"))
            {
                // Main file missing/corrupted — restore from backup
                var json = File.ReadAllText(SettingsPath + ".bak");
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Try backup if main file is corrupted
            try
            {
                if (File.Exists(SettingsPath + ".bak"))
                {
                    var json = File.ReadAllText(SettingsPath + ".bak");
                    Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    return;
                }
            }
            catch { /* backup also bad — fall through */ }

            Settings = new AppSettings();
        }
    }

    public void AddRecentConnection(SavedConnection connection)
    {
        // Remove existing entry for same server/database combo
        Settings.RecentConnections.RemoveAll(c =>
            c.Server == connection.Server && c.Database == connection.Database);

        // Add to front
        Settings.RecentConnections.Insert(0, connection);

        // Keep only max entries
        var max = Settings.MaxRecentConnections;
        if (Settings.RecentConnections.Count > max)
        {
            Settings.RecentConnections.RemoveRange(max,
                Settings.RecentConnections.Count - max);
        }

        Save();
    }

    public void AddRecentQuery(string path)
    {
        Settings.RecentQueryPaths.RemoveAll(p => p == path);
        Settings.RecentQueryPaths.Insert(0, path);

        if (Settings.RecentQueryPaths.Count > 10)
            Settings.RecentQueryPaths.RemoveRange(10, Settings.RecentQueryPaths.Count - 10);

        Save();
    }

    public List<string> GetRecentQueries() => Settings.RecentQueryPaths;

    public List<SavedConnection> GetNamedConnections()
        => Settings.RecentConnections.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();

    public void SaveLastComparison(SavedConnection? source, SavedConnection? target)
    {
        Settings.LastSourceServer = source?.Server;
        Settings.LastSourceDatabase = source?.Database;
        Settings.LastTargetServer = target?.Server;
        Settings.LastTargetDatabase = target?.Database;
        Save();
    }

    public (SavedConnection? Source, SavedConnection? Target) GetLastComparison()
    {
        SavedConnection? source = null;
        SavedConnection? target = null;

        if (!string.IsNullOrEmpty(Settings.LastSourceServer))
        {
            source = Settings.RecentConnections.FirstOrDefault(c =>
                c.Server == Settings.LastSourceServer && c.Database == Settings.LastSourceDatabase);
        }

        if (!string.IsNullOrEmpty(Settings.LastTargetServer))
        {
            target = Settings.RecentConnections.FirstOrDefault(c =>
                c.Server == Settings.LastTargetServer && c.Database == Settings.LastTargetDatabase);
        }

        return (source, target);
    }
}

public class AppSettings
{
    public List<SavedConnection> RecentConnections { get; set; } = new();
    public List<string> RecentQueryPaths { get; set; } = new();

    // Window position/size
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }

    // Last comparison pair
    public string? LastSourceServer { get; set; }
    public string? LastSourceDatabase { get; set; }
    public string? LastTargetServer { get; set; }
    public string? LastTargetDatabase { get; set; }

    // User preferences
    public bool UseDarkTheme { get; set; } = false;
    public int FontSize { get; set; } = 12;
    public int MaxRecentConnections { get; set; } = 5;
    public string? DataFolderPath { get; set; }
    public bool AutocompleteEnabled { get; set; } = true;
    public int GridRowHeight { get; set; } = 22;
    public int ConnectionTimeout { get; set; } = 5;

    // Formatter engine selection. Default = ScriptDom-based visitor formatter (recommended).
    // Set to false to use the legacy Hogimn regex formatter (kept as a permanent fallback).
    // LOOKOUT_USE_NEW_FORMATTER env var still honored at startup (overrides this setting when set).
    public bool UseNewFormatter { get; set; } = true;

    // User mode (Normal hides admin-only settings sections)
    public bool IsAdminMode { get; set; }

    // DDL Audit Source (Version History)
    public string? DdlAuditServer { get; set; }
    public string? DdlAuditDatabase { get; set; }
    public string? DdlAuditTable { get; set; }

    // Git Integration
    public string? GitExportPath { get; set; }
    public bool GitIncludeSystemDatabases { get; set; }

    public bool IsDdlAuditConfigured =>
        !string.IsNullOrWhiteSpace(DdlAuditDatabase) && !string.IsNullOrWhiteSpace(DdlAuditTable);

    // Panel collapse state
    public bool ObjectExplorerCollapsed { get; set; }
    public double ObjectExplorerWidth { get; set; } = 220;
    public bool ResultsPanelCollapsed { get; set; }
    public double ResultsPanelHeight { get; set; } = 200;
}

public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string Environment { get; set; } = "Unknown";
    public bool TrustServerCertificate { get; set; } = true;
    public int SortOrder { get; set; }
    public bool ConnectOnStartup { get; set; }
    public string DisplayName => Name != null
        ? $"{Name} ({Server}) / {Database} ({(UseWindowsAuth ? "Windows" : Username)})"
        : $"{Server} / {Database} ({(UseWindowsAuth ? "Windows" : Username)})";
}
