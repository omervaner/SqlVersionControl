using PlanViewer.Core.Models;

namespace SqlVersionControl.Services;

/// <summary>
/// Translates raw SQL Server operator names into plain-language labels
/// that support staff can understand at a glance.
/// </summary>
public static class PlanTranslator
{
    public static string Translate(PlanNode node)
    {
        var op = node.PhysicalOp.ToUpperInvariant();
        var logical = node.LogicalOp.ToUpperInvariant();

        // --- Scans & Seeks ---

        if (op.Contains("CLUSTERED INDEX SCAN"))
        {
            var table = ShortTable(node.ObjectName);
            return $"Reading entire table: {table} (slow \u2014 no filter used)";
        }

        if (op.Contains("CLUSTERED INDEX SEEK"))
        {
            var table = ShortTable(node.ObjectName);
            var index = node.IndexName ?? "clustered key";
            return $"Fast lookup on {table} using {index}";
        }

        if (op.Contains("INDEX SEEK"))
        {
            var table = ShortTable(node.ObjectName);
            var index = node.IndexName ?? "index";
            return $"Fast index lookup: {index} on {table}";
        }

        if (op.Contains("INDEX SCAN"))
        {
            var table = ShortTable(node.ObjectName);
            var index = node.IndexName ?? "index";
            return $"Scanning index: {index} on {table}";
        }

        if (op.Contains("TABLE SCAN"))
        {
            var table = ShortTable(node.ObjectName);
            return $"Full table read: {table} (no index available)";
        }

        if (op.Contains("KEY LOOKUP"))
        {
            var table = ShortTable(node.ObjectName);
            return $"Fetching extra columns from {table} (bookmark lookup)";
        }

        if (op.Contains("RID LOOKUP"))
        {
            var table = ShortTable(node.ObjectName);
            return $"Fetching row from heap: {table}";
        }

        // --- Joins ---

        if (op.Contains("HASH MATCH"))
        {
            if (logical.Contains("JOIN") || logical.Contains("SEMI") || logical.Contains("ANTI"))
            {
                var (left, right) = ResolveJoinTables(node);
                var joinType = node.EstimatedJoinType ?? logical.Replace(" JOIN", "").Trim().ToLowerInvariant();
                return $"Joining {left} to {right} ({joinType})";
            }

            // Aggregate variant
            if (logical.Contains("AGGREGATE") || logical.Contains("GROUP"))
            {
                var cols = Truncate(node.HashKeysBuild ?? node.GroupBy);
                return cols != null
                    ? $"Grouping results by {cols}"
                    : "Grouping results";
            }

            // Distinct / Union / other hash match
            return $"Hashing: {node.LogicalOp}";
        }

        if (op.Contains("NESTED LOOPS"))
        {
            var (left, right) = ResolveJoinTables(node);
            return $"Looking up {right} for each row in {left}";
        }

        if (op.Contains("MERGE JOIN"))
        {
            var (left, right) = ResolveJoinTables(node);
            return $"Merging sorted {left} with {right}";
        }

        // --- Sort & Filter ---

        if (op.Contains("SORT"))
        {
            var cols = Truncate(node.OrderBy);
            return cols != null
                ? $"Sorting results by {cols}"
                : "Sorting results";
        }

        if (op == "FILTER")
        {
            var pred = Truncate(node.Predicate);
            return pred != null
                ? $"Filtering rows where {pred}"
                : "Filtering rows";
        }

        // --- Compute & Aggregate ---

        if (op.Contains("COMPUTE SCALAR"))
            return "Calculating values";

        if (op.Contains("STREAM AGGREGATE"))
        {
            var cols = Truncate(node.GroupBy);
            return cols != null
                ? $"Aggregating on {cols}"
                : "Aggregating results";
        }

        // --- Parallelism ---

        if (op.Contains("PARALLELISM"))
        {
            var pType = (node.PartitioningType ?? node.LogicalOp ?? "").ToUpperInvariant();
            if (pType.Contains("GATHER"))
                return "Combining parallel threads";
            if (pType.Contains("REPARTITION") || pType.Contains("DISTRIBUTE"))
                return "Redistributing parallel threads";
            if (pType.Contains("ROUND ROBIN"))
                return "Splitting work across parallel threads";
            return "Combining/Redistributing parallel threads";
        }

        // --- Top ---

        if (op.Contains("TOP"))
        {
            if (node.TopRows > 0)
            {
                var suffix = node.IsPercent ? "%" : " rows";
                return $"Taking first {node.TopRows}{suffix}";
            }
            var expr = Truncate(node.TopExpression);
            return expr != null
                ? $"Taking first {expr} rows"
                : "Taking first N rows";
        }

        // --- Miscellaneous ---

        if (op.Contains("CONSTANT SCAN"))
            return "Generating constant values";

        if (op.Contains("SPOOL"))
            return "Caching intermediate results";

        if (op.Contains("CONCATENATION"))
            return "Combining multiple result sets";

        if (op.Contains("SEQUENCE"))
            return "Executing operations in sequence";

        // --- Fallback ---
        return string.Equals(node.PhysicalOp, node.LogicalOp, System.StringComparison.OrdinalIgnoreCase)
            ? node.PhysicalOp
            : $"{node.PhysicalOp} ({node.LogicalOp})";
    }

    /// <summary>
    /// Strips schema prefix: "dbo.Orders" → "Orders", "[dbo].[Orders]" → "Orders"
    /// </summary>
    private static string ShortTable(string? objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return "table";

        var name = objectName;
        // Strip brackets
        name = name.Replace("[", "").Replace("]", "");
        // Take last segment after dot
        var dotIdx = name.LastIndexOf('.');
        return dotIdx >= 0 ? name[(dotIdx + 1)..] : name;
    }

    /// <summary>
    /// Walks Children[0] and Children[1] subtrees depth-first to find the first node
    /// with an ObjectName, giving readable names for join inputs.
    /// </summary>
    private static (string Left, string Right) ResolveJoinTables(PlanNode node)
    {
        var left = node.Children.Count > 0
            ? FindFirstTable(node.Children[0]) ?? "input"
            : "input";
        var right = node.Children.Count > 1
            ? FindFirstTable(node.Children[1]) ?? "input"
            : "input";
        return (left, right);
    }

    private static string? FindFirstTable(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectName))
            return ShortTable(node.ObjectName);
        foreach (var child in node.Children)
        {
            var found = FindFirstTable(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Caps long predicate/expression strings at maxLen characters.
    /// </summary>
    private static string? Truncate(string? value, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLen ? value : value[..maxLen] + "\u2026";
    }
}
