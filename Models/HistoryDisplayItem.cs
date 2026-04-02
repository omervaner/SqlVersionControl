using SqlVersionControl.Helpers;
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

    public string TimeAgo => TimeFormatter.FormatTimeAgo(Entry.ExecutedAt);
}
