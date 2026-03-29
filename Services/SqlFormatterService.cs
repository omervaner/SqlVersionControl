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
    /// <summary>
    /// Format SQL text using T-SQL dialect with standard formatting rules.
    /// Keywords uppercased, 4-space indent, 2 blank lines between queries.
    /// </summary>
    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var config = FormatConfig.Builder()
            .Uppercase(true)
            .LinesBetweenQueries(2)
            .MaxColumnLength(80)
            .Build();

        return SqlFormatter.Of(Dialect.TSql).Format(sql, config);
    }
}
