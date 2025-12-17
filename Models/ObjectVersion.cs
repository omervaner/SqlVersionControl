namespace SqlVersionControl.Models;

public class ObjectVersion
{
    public int VersionId { get; set; }
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string Definition { get; set; } = "";
    public string EventType { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    public string HostName { get; set; } = "";
    public string? IPAddress { get; set; }
    public string? AppName { get; set; }
    public DateTime ChangedAt { get; set; }
    public int VersionNumber { get; set; }

    // Display helpers
    public string FullName => $"{SchemaName}.{ObjectName}";
    public string ChangedAtDisplay => ChangedAt.ToString("MMM dd, HH:mm");
    public string VersionDisplay => $"v{VersionNumber}";
    public string VersionLabel => $"v{VersionNumber} - {ChangedBy}@{HostName} - {ChangedAt:MMM dd, HH:mm}";
}

public class DatabaseObject
{
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public int VersionCount { get; set; }
    public DateTime? LastChanged { get; set; }

    public string FullName => $"{SchemaName}.{ObjectName}";
    public string DisplayInfo => $"{SchemaName} | {ObjectType} | {VersionCount} version(s)";

    public string TypeIcon => ObjectType.ToUpperInvariant() switch
    {
        "PROCEDURE" or "SQL_STORED_PROCEDURE" => "⚙",
        "FUNCTION" or "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" => "ƒ",
        "VIEW" => "👁",
        "TABLE" or "USER_TABLE" => "▤",
        "TRIGGER" or "SQL_TRIGGER" => "⚡",
        "INDEX" => "⇅",
        _ => "○"
    };
}

public class RecentChange
{
    public int VersionId { get; set; }
    public string ObjectName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string EventType { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public int VersionNumber { get; set; }

    public string FullName => $"{SchemaName}.{ObjectName}";
    public string ChangedAtDisplay => ChangedAt.ToString("MMM dd, HH:mm");

    public string TimeAgo
    {
        get
        {
            var diff = DateTime.Now - ChangedAt;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return ChangedAt.ToString("MMM dd");
        }
    }
}
