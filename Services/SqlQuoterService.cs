namespace SqlVersionControl.Services;

/// <summary>
/// Shared string manipulation for SQL Quoter dialog and Quick Quote toolbar button.
/// Converts a list of values (one per line or whitespace-separated) into SQL-ready formats.
/// </summary>
public static class SqlQuoterService
{
    public enum QuoteFormat
    {
        String,         // 'val1', 'val2', 'val3'
        Numeric,        // 1, 2, 3
        Parenthesized,  // ('val1', 'val2', 'val3')
        NString         // N'val1', N'val2', N'val3'
    }

    /// <summary>
    /// Parse raw input into trimmed, non-empty values.
    /// Splits on newlines; each line is trimmed of whitespace.
    /// </summary>
    public static List<string> ParseValues(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Format parsed values into SQL-ready output.
    /// </summary>
    public static string FormatValues(List<string> values, QuoteFormat format)
    {
        if (values.Count == 0)
            return "";

        var formatted = format switch
        {
            QuoteFormat.Numeric => string.Join(", ", values),
            QuoteFormat.NString => string.Join(", ", values.Select(v => $"N'{EscapeSql(v)}'")),
            QuoteFormat.Parenthesized => "(" + string.Join(", ", values.Select(v => $"'{EscapeSql(v)}'")) + ")",
            _ => string.Join(", ", values.Select(v => $"'{EscapeSql(v)}'")), // String (default)
        };

        return formatted;
    }

    /// <summary>
    /// Quick quote for toolbar button: always single-quotes, one value per line.
    /// Keeps values vertical so long lists are easy to scroll.
    /// </summary>
    public static string QuickQuote(string input, bool nPrefix = false)
    {
        var values = ParseValues(input);
        if (values.Count == 0)
            return "";

        var prefix = nPrefix ? "N" : "";
        var quoted = values.Select(v => $"{prefix}'{EscapeSql(v)}'");
        return string.Join(",\n", quoted);
    }

    private static bool IsNumeric(string value)
    {
        // Allow integers, decimals, negative numbers
        return decimal.TryParse(value, out _);
    }

    private static string EscapeSql(string value)
    {
        // Escape single quotes by doubling them
        return value.Replace("'", "''");
    }
}
