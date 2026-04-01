using System.Text;
using System.Text.RegularExpressions;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;

namespace SqlVersionControl.Services;

/// <summary>
/// SQL formatting service using Hogimn.Sql.Formatter (T-SQL dialect).
/// Wraps the library with app-standard formatting options.
/// </summary>
public static class SqlFormatterService
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

    /// <summary>
    /// Format SQL text using T-SQL dialect with standard formatting rules.
    /// Keywords uppercased, 4-space indent, blank lines between statements.
    /// </summary>
    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        // Split into individual statements, format each, rejoin with blank lines
        var statements = SplitStatements(sql);
        if (statements.Count <= 1)
            return SqlFormatter.Of(Dialect.TSql).Format(sql, Config);

        var sb = new StringBuilder();
        foreach (var stmt in statements)
        {
            var trimmed = stmt.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Strip trailing semicolons before formatting (formatter handles the rest)
            var toFormat = trimmed.TrimEnd(';').Trim();
            if (string.IsNullOrWhiteSpace(toFormat)) continue;

            if (sb.Length > 0)
                sb.AppendLine().AppendLine(); // blank line separator

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

        // If regex didn't split anything useful, return the whole thing
        return statements.Count > 0 ? statements : [sql];
    }
}
