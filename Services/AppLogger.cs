namespace SqlVersionControl.Services;

public static class AppLogger
{
    private static readonly string LogFolder = Path.Combine(SettingsService.DefaultDataFolder, "logs");
    private static readonly string LogPath = Path.Combine(LogFolder, "app.log");
    private static readonly string OldLogPath = Path.Combine(LogFolder, "app.log.old");
    private const long MaxLogSize = 5 * 1024 * 1024; // 5MB
    private static readonly object Lock = new();

    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            RotateIfNeeded();
        }
        catch
        {
            // Can't log if we can't create the log folder — fail silently
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw
        }
    }

    public static void LogError(string context, Exception ex)
    {
        Log($"ERROR [{context}] {ex.Message}");
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var info = new FileInfo(LogPath);
            if (info.Length <= MaxLogSize) return;

            if (File.Exists(OldLogPath))
                File.Delete(OldLogPath);
            File.Move(LogPath, OldLogPath);
        }
        catch
        {
            // Rotation failure is non-fatal
        }
    }
}
