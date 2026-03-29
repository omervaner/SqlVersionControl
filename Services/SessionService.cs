using System.Text.Json;

namespace SqlVersionControl.Services;

public class SessionService
{
    private static readonly string SessionPath = Path.Combine(
        SettingsService.DefaultDataFolder, "session.json");

    private SessionData _data = new();

    public SessionService()
    {
        Load();
    }

    public SessionData Data => _data;

    private void Load()
    {
        try
        {
            if (File.Exists(SessionPath))
            {
                var json = File.ReadAllText(SessionPath);
                _data = JsonSerializer.Deserialize<SessionData>(json) ?? new SessionData();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SessionService.Load", ex);
            _data = new SessionData();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SessionPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("SessionService.Save", ex);
        }
    }

    public void SaveTabs(List<TabState> tabs, int activeIndex)
    {
        _data.Tabs = tabs;
        _data.ActiveTabIndex = activeIndex;
        Save();
    }

    public void AddQueryToHistory(string sql, string? database, int rowCount)
    {
        var trimmed = sql.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        // Deduplicate: remove existing entry with same SQL text
        _data.QueryHistory.RemoveAll(h =>
            h.SqlText.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        // Insert at front
        _data.QueryHistory.Insert(0, new HistoryEntry
        {
            SqlText = trimmed,
            Database = database,
            ExecutedAt = DateTime.Now,
            RowCount = rowCount
        });

        // Cap at 200
        if (_data.QueryHistory.Count > 200)
            _data.QueryHistory.RemoveRange(200, _data.QueryHistory.Count - 200);

        Save();
    }

    public List<HistoryEntry> GetQueryHistory() => _data.QueryHistory;
}

public class SessionData
{
    public List<TabState> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }
    public List<HistoryEntry> QueryHistory { get; set; } = [];
}

public class TabState
{
    public string SqlText { get; set; } = "";
    public string? SelectedDatabase { get; set; }
    public string? SavedPath { get; set; }
    public string? QueryName { get; set; }
    public int CursorPosition { get; set; }

    // Per-tab connection (v1.6.0)
    public string? ConnectionServer { get; set; }
    public string? ConnectionDatabase { get; set; }
    public string? ConnectionUsername { get; set; }
    public bool? ConnectionUseWindowsAuth { get; set; }
    public string? ConnectionProfileName { get; set; }
    public string? ConnectionProfileColor { get; set; }
}

public class HistoryEntry
{
    public string SqlText { get; set; } = "";
    public string? Database { get; set; }
    public DateTime ExecutedAt { get; set; }
    public int RowCount { get; set; }
}
