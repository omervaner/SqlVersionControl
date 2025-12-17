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
    public bool UseDarkTheme { get; set; } = true;
    public int FontSize { get; set; } = 12;
    public int MaxRecentConnections { get; set; } = 5;
    public string? DataFolderPath { get; set; }
}

public class SavedConnection
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public string DisplayName => $"{Server} / {Database} ({Username})";
}
