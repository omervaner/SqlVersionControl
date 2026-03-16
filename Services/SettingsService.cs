using System.Text.Json;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class SettingsService
{
    public static readonly string DefaultDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlVersionControl");

    private static readonly string SettingsPath = Path.Combine(DefaultDataFolder, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Load();
        PasswordStore.Load();
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
        }
        catch
        {
            Settings = new AppSettings();
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
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save settings: {ex.Message}");
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

    // Panel collapse state
    public bool ObjectExplorerCollapsed { get; set; }
    public double ObjectExplorerWidth { get; set; } = 220;
    public bool ResultsPanelCollapsed { get; set; }
    public double ResultsPanelHeight { get; set; } = 200;
}

public class SavedConnection
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string DisplayName => Name != null
        ? $"{Name} ({Server}) / {Database} ({(UseWindowsAuth ? "Windows" : Username)})"
        : $"{Server} / {Database} ({(UseWindowsAuth ? "Windows" : Username)})";
}
