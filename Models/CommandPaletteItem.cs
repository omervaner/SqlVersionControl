namespace SqlVersionControl.Models;

public class CommandPaletteItem
{
    public string Name { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string Description { get; set; } = "";
    public Action Execute { get; set; } = () => { };

    /// <summary>
    /// Fuzzy match score — higher is better. Returns 0 if no match.
    /// </summary>
    public int FuzzyMatch(string query)
    {
        if (string.IsNullOrEmpty(query)) return 1; // show all when empty

        var target = Name.ToLowerInvariant();
        var q = query.ToLowerInvariant();

        // Exact substring match (highest score)
        if (target.Contains(q))
            return 100 + (target.StartsWith(q) ? 50 : 0);

        // Fuzzy: all query chars appear in order
        var qi = 0;
        for (var ti = 0; ti < target.Length && qi < q.Length; ti++)
        {
            if (target[ti] == q[qi]) qi++;
        }

        return qi == q.Length ? 50 : 0;
    }
}
