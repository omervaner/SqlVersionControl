using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Services;

/// <summary>
/// Exports all database object definitions as .sql files to a local folder.
/// Each export is a full snapshot — the folder always reflects the current state of the server.
/// </summary>
public class GitExportService
{
    private readonly DatabaseService _db;

    public GitExportService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>Progress callback: (message, currentDb, totalDbs)</summary>
    public event Action<string, int, int>? ProgressChanged;

    public async Task<ExportResult> ExportAsync(
        string connectionString, string exportPath, bool includeSystemDatabases,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExportResult();
        var serverName = SanitizeServerName(connectionString);
        var serverPath = Path.Combine(exportPath, serverName);

        // Get server/user info for changelog
        var builder = new SqlConnectionStringBuilder(connectionString);
        result.ServerDisplay = builder.DataSource;
        result.UserDisplay = string.IsNullOrEmpty(builder.UserID) ? "Windows Auth" : builder.UserID;

        // Enumerate databases
        List<string> databases;
        try
        {
            databases = await _db.GetAllDatabasesAsync(connectionString, includeSystemDatabases);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to enumerate databases: {ex.Message}");
            return result;
        }

        result.TotalDatabases = databases.Count;

        // Track all expected file paths so we can detect deletions
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < databases.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var dbName = databases[i];
            ProgressChanged?.Invoke($"Exporting {dbName}...", i + 1, databases.Count);

            try
            {
                var dbChanges = await ExportDatabaseAsync(connectionString, exportPath, serverName, dbName, expectedFiles, ct);
                result.Added += dbChanges.Added;
                result.Modified += dbChanges.Modified;
                result.Changes.AddRange(dbChanges.Changes);

                if (dbChanges.Added > 0 || dbChanges.Modified > 0)
                    result.DatabasesWithChanges++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"[{dbName}] {ex.Message}");
                result.Changes.Add($"- **Skipped:** {dbName} — {ex.Message}");
            }
        }

        // Cleanup: delete .sql files that no longer correspond to server objects
        var deleted = CleanupDeletedFiles(serverPath, expectedFiles, result);
        result.Deleted += deleted;

        if (deleted > 0)
            result.DatabasesWithChanges = Math.Min(result.DatabasesWithChanges + 1, result.TotalDatabases);

        // Write changelog
        sw.Stop();
        result.Duration = sw.Elapsed;
        WriteChangelog(exportPath, result);

        return result;
    }

    private async Task<(int Added, int Modified, List<string> Changes)> ExportDatabaseAsync(
        string connectionString, string exportPath, string serverName, string dbName,
        HashSet<string> expectedFiles, CancellationToken ct)
    {
        int added = 0, modified = 0;
        var changes = new List<string>();

        // 1. Code objects (procs, functions, views, triggers)
        var codeObjects = await _db.GetAllCodeObjectsAsync(connectionString, dbName);
        foreach (var obj in codeObjects)
        {
            ct.ThrowIfCancellationRequested();
            var folder = MapTypeDescToFolder(obj.TypeDesc);
            if (folder == null) continue;

            var fileName = SanitizeFileName($"{obj.Schema}.{obj.Name}.sql");
            var filePath = Path.Combine(exportPath, serverName, dbName, folder, fileName);
            expectedFiles.Add(filePath);

            var changeType = WriteFileIfChanged(filePath, obj.Definition);
            if (changeType == ChangeType.Added)
            {
                added++;
                changes.Add($"- **Added:** {dbName}/{folder}/{fileName}");
            }
            else if (changeType == ChangeType.Modified)
            {
                modified++;
                changes.Add($"- **Modified:** {dbName}/{folder}/{fileName}");
            }
        }

        // 2. Tables (CREATE TABLE scripts from metadata)
        var tableColumns = await _db.GetAllTableColumnsAsync(connectionString, dbName);
        foreach (var kvp in tableColumns)
        {
            ct.ThrowIfCancellationRequested();
            var (schema, table) = kvp.Key;
            var columns = kvp.Value;

            var script = DatabaseService.GenerateCreateTableScript(schema, table, columns);
            var fileName = SanitizeFileName($"{schema}.{table}.sql");
            var filePath = Path.Combine(exportPath, serverName, dbName, "Tables", fileName);
            expectedFiles.Add(filePath);

            var changeType = WriteFileIfChanged(filePath, script);
            if (changeType == ChangeType.Added)
            {
                added++;
                changes.Add($"- **Added:** {dbName}/Tables/{fileName}");
            }
            else if (changeType == ChangeType.Modified)
            {
                modified++;
                changes.Add($"- **Modified:** {dbName}/Tables/{fileName}");
            }
        }

        return (added, modified, changes);
    }

    private enum ChangeType { None, Added, Modified }

    private static ChangeType WriteFileIfChanged(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var isNew = !File.Exists(filePath);

        if (!isNew)
        {
            var existing = File.ReadAllText(filePath);
            if (existing == content)
                return ChangeType.None;
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, content);
        return isNew ? ChangeType.Added : ChangeType.Modified;
    }

    private static int CleanupDeletedFiles(string serverPath, HashSet<string> expectedFiles, ExportResult result)
    {
        if (!Directory.Exists(serverPath))
            return 0;

        var deleted = 0;
        var allSqlFiles = Directory.GetFiles(serverPath, "*.sql", SearchOption.AllDirectories);

        foreach (var file in allSqlFiles)
        {
            if (!expectedFiles.Contains(file))
            {
                // Extract relative path for changelog
                var rel = Path.GetRelativePath(Path.GetDirectoryName(serverPath)!, file);
                result.Changes.Add($"- **Deleted:** {rel}");

                File.Delete(file);
                deleted++;

                // Clean up empty directories
                var dir = Path.GetDirectoryName(file)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }

        return deleted;
    }

    private static void WriteChangelog(string exportPath, ExportResult result)
    {
        var changelogPath = Path.Combine(exportPath, "CHANGELOG.md");
        var sb = new StringBuilder();

        sb.AppendLine($"## Export — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"**Server:** {result.ServerDisplay} ({result.UserDisplay})");
        sb.AppendLine($"**Databases:** {result.TotalDatabases} ({result.DatabasesWithChanges} with changes)");
        sb.AppendLine($"**Duration:** {result.Duration.TotalSeconds:F1}s");
        sb.AppendLine();

        if (result.Changes.Count > 0)
        {
            sb.AppendLine("### Changes");
            foreach (var change in result.Changes)
                sb.AppendLine(change);
            sb.AppendLine();
            sb.AppendLine($"**Summary:** {result.Added} added, {result.Modified} modified, {result.Deleted} deleted");
        }
        else
        {
            sb.AppendLine("No changes detected.");
        }

        if (result.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Warnings");
            foreach (var err in result.Errors)
                sb.AppendLine($"- {err}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Prepend to existing changelog (newest first), or create new
        if (File.Exists(changelogPath))
        {
            var existing = File.ReadAllText(changelogPath);
            File.WriteAllText(changelogPath, sb.ToString() + existing);
        }
        else
        {
            File.WriteAllText(changelogPath, sb.ToString());
        }
    }

    private static string? MapTypeDescToFolder(string typeDesc) => typeDesc switch
    {
        "SQL_STORED_PROCEDURE" => "StoredProcedures",
        "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION" => "Functions",
        "VIEW" => "Views",
        "SQL_TRIGGER" or "SQL_DML_TRIGGER" => "Triggers",
        _ => null
    };

    /// <summary>
    /// Sanitizes server name for filesystem use: replace \ with _, strip/convert port.
    /// </summary>
    private static string SanitizeServerName(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource;

        // Replace backslash (named instances like SERVER\INSTANCE)
        server = server.Replace('\\', '_');

        // Replace characters invalid in file/folder names
        foreach (var c in Path.GetInvalidFileNameChars())
            server = server.Replace(c, '_');

        // Commas in server,port format
        server = server.Replace(',', '_');

        return server;
    }

    private static string SanitizeFileName(string name)
    {
        // Dots in object names (beyond schema.name.sql) could cause issues
        // Split into parts: schema.objectname.sql
        // Only replace dots within the object name portion
        var parts = name.Split('.');
        if (parts.Length > 2)
        {
            // schema.name.with.dots.sql → schema.name_with_dots.sql
            var schema = parts[0];
            var ext = parts[^1]; // "sql"
            var objName = string.Join("_", parts[1..^1]);
            name = $"{schema}.{objName}.{ext}";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }
}

public class ExportResult
{
    public int TotalDatabases { get; set; }
    public int DatabasesWithChanges { get; set; }
    public int Added { get; set; }
    public int Modified { get; set; }
    public int Deleted { get; set; }
    public TimeSpan Duration { get; set; }
    public string ServerDisplay { get; set; } = "";
    public string UserDisplay { get; set; } = "";
    public List<string> Changes { get; } = new();
    public List<string> Errors { get; } = new();
    public int TotalChanges => Added + Modified + Deleted;
}
