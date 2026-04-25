using System.Text;
using System.Text.RegularExpressions;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;

namespace SqlVersionControl.Services.Formatting;

/// <summary>
/// Legacy Hogimn-based formatter — kept verbatim for the toggle period.
/// Removed one release after the new formatter is enabled by default.
/// </summary>
public static class LegacyHogimnFormatter
{
    private static readonly FormatConfig Config = FormatConfig.Builder()
        .Uppercase(true)
        .LinesBetweenQueries(2)
        .MaxColumnLength(80)
        .Build();

    // Matches statement-starting keywords at the beginning of a line (ignoring whitespace)
    private static readonly Regex StatementStart = new(
        @"(?<=;)\s*(?=\S)|(?<=\n|\r\n)(?=\s*(?:SELECT|INSERT|UPDATE|DELETE|MERGE|WITH|CREATE|ALTER|DROP|EXEC|EXECUTE|DECLARE|IF|BEGIN|WHILE|SET\s+NOCOUNT|PRINT|USE|GO)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var statements = SplitStatements(sql);
        if (statements.Count <= 1)
            return SqlFormatter.Of(Dialect.TSql).Format(sql, Config);

        var sb = new StringBuilder();
        foreach (var stmt in statements)
        {
            var trimmed = stmt.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var toFormat = trimmed.TrimEnd(';').Trim();
            if (string.IsNullOrWhiteSpace(toFormat)) continue;

            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            sb.Append(SqlFormatter.Of(Dialect.TSql).Format(toFormat, Config));
        }

        return sb.ToString();
    }

    private static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var parts = StatementStart.Split(sql);

        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
                statements.Add(part);
        }

        return statements.Count > 0 ? statements : [sql];
    }
}
