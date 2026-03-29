using System.Runtime.InteropServices;
using System.Text;

namespace SqlVersionControl.Services;

/// <summary>
/// Captures unhandled exceptions to structured crash log files.
/// On next startup, pending crash logs can be detected and surfaced to the user.
/// </summary>
public static class CrashLogger
{
    private static readonly string LogFolder = Path.Combine(SettingsService.DefaultDataFolder, "logs");
    private const int MaxCrashFiles = 5;

    // Context setters — called by MainWindow to keep crash context up to date
    public static string? ActiveConnection { get; set; }
    public static string? ActiveDatabase { get; set; }
    public static string? ActiveTabName { get; set; }
    public static string? LastQuery { get; set; }

    /// <summary>
    /// Write a crash report to disk. Safe to call from any thread, never throws.
    /// </summary>
    public static void LogCrash(Exception? ex)
    {
        if (ex == null) return;

        try
        {
            Directory.CreateDirectory(LogFolder);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var path = Path.Combine(LogFolder, $"crash-{timestamp}.log");

            var sb = new StringBuilder();
            sb.AppendLine("=== CRASH REPORT ===");
            sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Version: {AppVersion.Version}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");

            if (!string.IsNullOrEmpty(ActiveConnection))
                sb.AppendLine($"Connection: {ActiveConnection}");
            if (!string.IsNullOrEmpty(ActiveDatabase))
                sb.AppendLine($"Database: {ActiveDatabase}");

            sb.AppendLine();
            sb.AppendLine($"Exception: {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine($"Stack Trace:");
            sb.AppendLine(ex.StackTrace);

            // Inner exceptions
            var inner = ex.InnerException;
            while (inner != null)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Inner Exception ---");
                sb.AppendLine($"Exception: {inner.GetType().FullName}");
                sb.AppendLine($"Message: {inner.Message}");
                sb.AppendLine($"Stack Trace:");
                sb.AppendLine(inner.StackTrace);
                inner = inner.InnerException;
            }

            // Context
            sb.AppendLine();
            sb.AppendLine("=== CONTEXT ===");
            if (!string.IsNullOrEmpty(ActiveTabName))
                sb.AppendLine($"Active Tab: {ActiveTabName}");
            if (!string.IsNullOrEmpty(LastQuery))
            {
                var queryPreview = LastQuery.Length > 500 ? LastQuery[..500] + "..." : LastQuery;
                sb.AppendLine($"Last Query: {queryPreview}");
            }

            File.WriteAllText(path, sb.ToString());

            // Prune old crash files
            PruneOldCrashFiles();
        }
        catch
        {
            // Crash logging must never throw
        }
    }

    /// <summary>
    /// Returns the most recent crash log path, or null if none exist.
    /// </summary>
    public static string? GetLatestCrashReport()
    {
        try
        {
            if (!Directory.Exists(LogFolder)) return null;

            return Directory.GetFiles(LogFolder, "crash-*.log")
                .OrderByDescending(f => f)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the content of the most recent crash log, or null.
    /// </summary>
    public static string? ReadLatestCrashReport()
    {
        var path = GetLatestCrashReport();
        if (path == null) return null;

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes all crash log files (called after user dismisses the banner).
    /// </summary>
    public static void ClearCrashReports()
    {
        try
        {
            if (!Directory.Exists(LogFolder)) return;

            foreach (var file in Directory.GetFiles(LogFolder, "crash-*.log"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    /// <summary>
    /// Returns true if there are any pending crash reports from previous sessions.
    /// </summary>
    public static bool HasPendingCrashReports()
    {
        try
        {
            if (!Directory.Exists(LogFolder)) return false;
            return Directory.GetFiles(LogFolder, "crash-*.log").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneOldCrashFiles()
    {
        try
        {
            var files = Directory.GetFiles(LogFolder, "crash-*.log")
                .OrderByDescending(f => f)
                .Skip(MaxCrashFiles)
                .ToList();

            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Non-fatal
        }
    }
}
