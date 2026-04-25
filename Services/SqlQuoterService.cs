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
    /// Per-line heuristic: split on newlines first, then classify each line.
    /// Comma → split on comma. No letters + whitespace + no ':' / '/' / '.' → split on whitespace.
    /// Otherwise → single value (names with spaces, phrases, timestamps, IPs, slash-dates, decimals).
    /// </summary>
    public static List<string> ParseValues(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        input = input.Trim();

        // Strip ONE matched pair of wrapping parens or brackets (not greedy —
        // avoids eating legitimate trailing parens on values like "abc)")
        if (input.Length >= 2 &&
            ((input[0] == '(' && input[^1] == ')') ||
             (input[0] == '[' && input[^1] == ']')))
        {
            input = input.Substring(1, input.Length - 2).Trim();
        }

        var lines = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            string[] parts;
            if (line.Contains(','))
            {
                // Comma wins over whitespace — "1, 2, 3" splits cleanly
                parts = line.Split(',');
            }
            else if (!line.Any(char.IsLetter)
                     && line.Any(char.IsWhiteSpace)
                     && !line.Any(c => c == ':' || c == '/' || c == '.'))
            {
                // Numeric/ID line with whitespace and no structural markers → split on whitespace.
                // ':' / '/' / '.' mark timestamps, slash-dates, IPs, decimals, version numbers
                // as single structural values; suppressing the split keeps them whole.
                parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                // Single value (names with spaces, phrases, structured values, single token)
                parts = [line];
            }

            foreach (var p in parts)
            {
                var trimmed = p.Trim().Trim('\'', '"');
                if (trimmed.Length > 0) result.Add(trimmed);
            }
        }

        return result;
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
