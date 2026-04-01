using SqlVersionControl.Services;

namespace SqlVersionControl.Models;

public class HistoryDisplayItem
{
    public HistoryEntry Entry { get; }

    public HistoryDisplayItem(HistoryEntry entry)
    {
        Entry = entry;
    }

    public string TruncatedSql
    {
        get
        {
            var s = Entry.SqlText.ReplaceLineEndings(" ");
            return s.Length > 120 ? s[..117] + "..." : s;
        }
    }

    public string? Database => Entry.Database;
    public int RowCount => Entry.RowCount;

    public string TimeAgo
    {
        get
        {
            var span = DateTime.Now - Entry.ExecutedAt;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
            return Entry.ExecutedAt.ToString("MMM d");
        }
    }
}
