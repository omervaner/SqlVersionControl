using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public class QueryFileService
{
    private static readonly string QueriesFolder = Path.Combine(
        SettingsService.DefaultDataFolder, "queries");

    public void SaveQuery(string path, string name, string database, string sql)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Read existing Created date if file exists, otherwise use now
        var created = DateTime.UtcNow;
        if (File.Exists(path))
        {
            try
            {
                var existing = LoadQuery(path);
                created = existing.Created;
            }
            catch
            {
                // Ignore parse errors — use current time
            }
        }

        var modified = DateTime.UtcNow;

        var sb = new StringBuilder();
        sb.AppendLine("-- SqlVersionControl Query");
        sb.AppendLine($"-- Name: {name}");
        sb.AppendLine($"-- Database: {database}");
        sb.AppendLine($"-- Created: {created:O}");
        sb.AppendLine($"-- Modified: {modified:O}");
        sb.AppendLine();
        sb.Append(sql);

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public (string Name, string Database, string Sql, DateTime Created, DateTime Modified) LoadQuery(string path)
    {
        var lines = File.ReadAllLines(path);

        string name = Path.GetFileNameWithoutExtension(path);
        string database = "";
        var created = DateTime.UtcNow;
        var modified = DateTime.UtcNow;
        int headerEnd = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("--"))
            {
                // First non-comment line (or blank after header) marks end of header
                headerEnd = i;
                // Skip one blank line after header if present
                if (string.IsNullOrWhiteSpace(line) && i + 1 <= lines.Length)
                    headerEnd = i + 1;
                break;
            }

            if (line.StartsWith("-- Name: "))
                name = line["-- Name: ".Length..].Trim();
            else if (line.StartsWith("-- Database: "))
                database = line["-- Database: ".Length..].Trim();
            else if (line.StartsWith("-- Created: "))
                DateTime.TryParse(line["-- Created: ".Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created);
            else if (line.StartsWith("-- Modified: "))
                DateTime.TryParse(line["-- Modified: ".Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out modified);

            headerEnd = i + 1;
        }

        var sql = string.Join(Environment.NewLine, lines.Skip(headerEnd));

        return (name, database, sql, created, modified);
    }

    public List<QueryFileInfo> ListSavedQueries()
    {
        var result = new List<QueryFileInfo>();
        if (!Directory.Exists(QueriesFolder))
            return result;

        foreach (var file in Directory.GetFiles(QueriesFolder, "*.sql"))
        {
            try
            {
                var (name, database, _, created, modified) = LoadQuery(file);
                result.Add(new QueryFileInfo
                {
                    Name = name,
                    Database = database,
                    FilePath = file,
                    Created = created,
                    Modified = modified
                });
            }
            catch
            {
                // Skip files that can't be parsed
            }
        }

        return result.OrderByDescending(q => q.Modified).ToList();
    }

    public void DeleteQuery(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public string GenerateFilePath(string name)
    {
        // Sanitize name for filename
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "query";

        var path = Path.Combine(QueriesFolder, sanitized + ".sql");

        // If file already exists, append a number
        if (File.Exists(path))
        {
            int counter = 2;
            while (File.Exists(Path.Combine(QueriesFolder, $"{sanitized} ({counter}).sql")))
                counter++;
            path = Path.Combine(QueriesFolder, $"{sanitized} ({counter}).sql");
        }

        return path;
    }
}
