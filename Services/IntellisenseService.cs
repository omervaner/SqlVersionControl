using System.Text.RegularExpressions;

namespace SqlVersionControl.Services;

public class IntellisenseService
{
    private List<(string Schema, string Name)> _tables = [];
    private List<(string Schema, string Name)> _views = [];
    private Dictionary<string, List<string>> _allColumns = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    // All SQL keywords from SQL.xshd — static, loaded once
    private static readonly string[] SqlKeywords =
    [
        // DDL
        "CREATE", "ALTER", "DROP", "TRUNCATE",
        "TABLE", "VIEW", "INDEX", "PROCEDURE",
        "PROC", "FUNCTION", "TRIGGER", "SCHEMA",
        "DATABASE", "CONSTRAINT", "PRIMARY", "FOREIGN",
        "KEY", "REFERENCES", "UNIQUE", "CHECK",
        "DEFAULT", "IDENTITY", "CLUSTERED", "NONCLUSTERED",
        // DML
        "SELECT", "INSERT", "UPDATE", "DELETE",
        "MERGE", "INTO", "VALUES", "FROM",
        "WHERE", "SET", "OUTPUT", "INSERTED", "DELETED",
        // Clauses
        "JOIN", "INNER", "LEFT", "RIGHT",
        "FULL", "OUTER", "CROSS", "ON",
        "AND", "OR", "NOT", "IN",
        "EXISTS", "BETWEEN", "LIKE", "IS",
        "NULL", "AS", "CASE", "WHEN",
        "THEN", "ELSE", "END", "GROUP",
        "BY", "HAVING", "ORDER", "ASC",
        "DESC", "TOP", "DISTINCT", "ALL",
        "UNION", "INTERSECT", "EXCEPT", "WITH",
        "OVER", "PARTITION", "ROW_NUMBER", "RANK", "DENSE_RANK",
        // Control flow
        "IF", "BEGIN", "WHILE", "BREAK",
        "CONTINUE", "RETURN", "GOTO", "WAITFOR",
        "TRY", "CATCH", "THROW", "RAISERROR",
        // Transactions
        "TRANSACTION", "TRAN", "COMMIT", "ROLLBACK",
        "SAVE", "SAVEPOINT",
        // Variables and declarations
        "DECLARE", "EXEC", "EXECUTE", "PRINT",
        "USE", "GO",
        // Data types
        "INT", "BIGINT", "SMALLINT", "TINYINT",
        "BIT", "DECIMAL", "NUMERIC", "MONEY",
        "SMALLMONEY", "FLOAT", "REAL", "DATETIME",
        "DATETIME2", "DATE", "TIME", "SMALLDATETIME",
        "DATETIMEOFFSET", "CHAR", "VARCHAR", "TEXT",
        "NCHAR", "NVARCHAR", "NTEXT", "BINARY",
        "VARBINARY", "IMAGE", "UNIQUEIDENTIFIER", "XML",
        "CURSOR", "SQL_VARIANT", "TIMESTAMP", "ROWVERSION",
        // Other
        "NOCOUNT", "NOLOCK", "ROWLOCK", "TABLOCK",
        "HOLDLOCK", "UPDLOCK", "XLOCK", "FETCH",
        "NEXT", "PRIOR", "FIRST", "LAST",
        "ABSOLUTE", "RELATIVE", "OPEN", "CLOSE",
        "DEALLOCATE", "FOR", "READONLY", "OUT", "MAX",
        "ISNULL", "COALESCE", "CAST", "CONVERT",
        "GETDATE", "DATEADD", "DATEDIFF", "COUNT",
        "SUM", "AVG", "MIN", "LEN",
        "SUBSTRING", "REPLACE", "LTRIM", "RTRIM",
        "UPPER", "LOWER", "CHARINDEX", "PATINDEX",
        "STUFF", "OBJECT_ID", "SCHEMA_NAME"
    ];

    private static readonly Regex FromJoinRegex = new(
        @"(?:FROM|JOIN)\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AfterFromJoinRegex = new(
        @"(?:FROM|JOIN|INTO)\s+\w*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AfterClauseRegex = new(
        @"(?:SELECT|WHERE|ORDER\s+BY|GROUP\s+BY|HAVING|ON|SET|AND|OR)\s+\w*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Capture which clause keyword we matched so we can filter keywords by context
    private static readonly Regex ClauseCapture = new(
        @"(SELECT|WHERE|ORDER\s+BY|GROUP\s+BY|HAVING|ON|SET|AND|OR)\s+\w*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Context-specific keyword subsets
    private static readonly HashSet<string> WhereKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL",
        "CASE", "WHEN", "THEN", "ELSE", "END",
        "ISNULL", "COALESCE", "CAST", "CONVERT",
        "GETDATE", "DATEADD", "DATEDIFF", "LEN", "SUBSTRING",
        "UPPER", "LOWER", "LTRIM", "RTRIM", "REPLACE", "CHARINDEX",
        "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    private static readonly HashSet<string> OrderByKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASC", "DESC", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ISNULL", "COALESCE", "CAST", "CONVERT", "LEN", "UPPER", "LOWER"
    };

    private static readonly HashSet<string> SelectKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOP", "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END", "AS",
        "ISNULL", "COALESCE", "CAST", "CONVERT",
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "OVER",
        "GETDATE", "DATEADD", "DATEDIFF", "LEN", "SUBSTRING",
        "UPPER", "LOWER", "LTRIM", "RTRIM", "REPLACE", "CHARINDEX", "STUFF"
    };

    public void SetSchema(
        List<(string Schema, string Name)> tables,
        List<(string Schema, string Name)> views,
        Dictionary<string, List<string>> allColumns)
    {
        _tables = tables;
        _views = views;
        _allColumns = allColumns;
        _isLoaded = true;
    }

    public void Clear()
    {
        _tables = [];
        _views = [];
        _allColumns = new(StringComparer.OrdinalIgnoreCase);
        _isLoaded = false;
    }

    public List<SqlCompletionData> GetCompletions(string fullText, int caretOffset)
    {
        if (caretOffset <= 0 || caretOffset > fullText.Length)
            return GetKeywordCompletions("");

        var textBefore = fullText[..caretOffset];

        // 1. Dot notation — tablename. or alias.
        if (caretOffset > 0 && fullText[caretOffset - 1] == '.')
            return GetDotCompletions(fullText, caretOffset);

        var (word, _) = GetWordBeforeCaret(fullText, caretOffset);

        // Text before the current word (includes trailing whitespace naturally)
        var beforeWord = caretOffset - word.Length > 0 ? fullText[..(caretOffset - word.Length)] : "";

        // 2. After FROM/JOIN/INTO — suggest tables and views
        if (AfterFromJoinRegex.IsMatch(beforeWord))
        {
            return GetTableCompletions(word);
        }

        // 3. After SELECT/WHERE/ORDER BY/etc — suggest columns from referenced tables + context keywords
        if (_isLoaded)
        {
            var clauseMatch = ClauseCapture.Match(beforeWord);
            if (clauseMatch.Success)
            {
                var fromTables = ParseFromTables(fullText, caretOffset);
                var clause = clauseMatch.Groups[1].Value.Trim().ToUpperInvariant();
                if (fromTables.Count > 0)
                {
                    var contextKeywords = clause switch
                    {
                        "SELECT" => SelectKeywords,
                        "WHERE" or "AND" or "OR" or "ON" or "HAVING" => WhereKeywords,
                        "SET" => WhereKeywords,
                        _ when clause.Contains("ORDER") => OrderByKeywords,
                        _ when clause.Contains("GROUP") => null, // columns only, no keywords
                        _ => null
                    };
                    return GetColumnAndKeywordCompletions(word, fromTables, contextKeywords);
                }
            }
        }

        // 4. Default — keywords + table names
        return GetDefaultCompletions(word);
    }

    private List<SqlCompletionData> GetDotCompletions(string fullText, int caretOffset)
    {
        if (!_isLoaded) return [];

        // Find the word before the dot
        var dotPos = caretOffset - 1;
        var start = dotPos;
        while (start > 0 && (char.IsLetterOrDigit(fullText[start - 1]) || fullText[start - 1] == '_'))
            start--;
        var wordBeforeDot = fullText[start..dotPos];

        if (string.IsNullOrEmpty(wordBeforeDot)) return [];

        // Try direct table match (schema.table or just table name)
        var columns = ResolveColumnsForName(wordBeforeDot, fullText, caretOffset);
        return columns
            .Select(c => new SqlCompletionData(c, CompletionCategory.Column))
            .ToList();
    }

    private List<string> ResolveColumnsForName(string name, string fullText, int caretOffset)
    {
        // First try: exact match as "schema.table" key
        foreach (var kvp in _allColumns)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Second try: match just the table part (e.g. "Orders" matches "dbo.Orders")
        foreach (var kvp in _allColumns)
        {
            var parts = kvp.Key.Split('.');
            if (parts.Length == 2 && parts[1].Equals(name, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Third try: alias resolution from FROM/JOIN clauses
        var fromTables = ParseFromTables(fullText, caretOffset);
        foreach (var (schemaTable, alias) in fromTables)
        {
            if (alias != null && alias.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (_allColumns.TryGetValue(schemaTable, out var cols))
                    return cols;
            }
            // Also match the table name itself (without schema prefix)
            var tableParts = schemaTable.Split('.');
            if (tableParts.Length == 2 && tableParts[1].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (_allColumns.TryGetValue(schemaTable, out var cols))
                    return cols;
            }
        }

        return [];
    }

    private List<SqlCompletionData> GetTableCompletions(string prefix)
    {
        var results = new List<SqlCompletionData>();

        if (_isLoaded)
        {
            foreach (var (schema, name) in _tables)
            {
                var display = $"[{schema}].[{name}]";
                if (MatchesPrefix(name, prefix) || MatchesPrefix(display, prefix))
                    results.Add(new SqlCompletionData(display, CompletionCategory.Table));
            }
            foreach (var (schema, name) in _views)
            {
                var display = $"[{schema}].[{name}]";
                if (MatchesPrefix(name, prefix) || MatchesPrefix(display, prefix))
                    results.Add(new SqlCompletionData(display, CompletionCategory.View));
            }
        }

        return results;
    }

    private List<SqlCompletionData> GetColumnAndKeywordCompletions(
        string prefix, List<(string schemaTable, string? alias)> fromTables,
        HashSet<string>? contextKeywords)
    {
        var results = new List<SqlCompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add columns from referenced tables
        foreach (var (schemaTable, _) in fromTables)
        {
            if (_allColumns.TryGetValue(schemaTable, out var cols))
            {
                foreach (var col in cols)
                {
                    if (MatchesPrefix(col, prefix) && seen.Add(col))
                        results.Add(new SqlCompletionData(col, CompletionCategory.Column));
                }
            }
        }

        // Add context-appropriate keywords (null = columns only)
        if (contextKeywords != null)
            AddFilteredKeywords(results, prefix, seen, contextKeywords);

        return results;
    }

    private List<SqlCompletionData> GetDefaultCompletions(string prefix)
    {
        var results = new List<SqlCompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add table/view names (short names for general context)
        if (_isLoaded)
        {
            foreach (var (_, name) in _tables)
            {
                if (MatchesPrefix(name, prefix) && seen.Add(name))
                    results.Add(new SqlCompletionData(name, CompletionCategory.Table));
            }
            foreach (var (_, name) in _views)
            {
                if (MatchesPrefix(name, prefix) && seen.Add(name))
                    results.Add(new SqlCompletionData(name, CompletionCategory.View));
            }
        }

        AddKeywords(results, prefix, seen);
        return results;
    }

    private List<SqlCompletionData> GetKeywordCompletions(string prefix)
    {
        var results = new List<SqlCompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeywords(results, prefix, seen);
        return results;
    }

    private static void AddKeywords(List<SqlCompletionData> results, string prefix, HashSet<string> seen)
    {
        foreach (var kw in SqlKeywords)
        {
            if (MatchesPrefix(kw, prefix) && seen.Add(kw))
                results.Add(new SqlCompletionData(kw, CompletionCategory.Keyword));
        }
    }

    private static void AddFilteredKeywords(List<SqlCompletionData> results, string prefix,
        HashSet<string> seen, HashSet<string> allowed)
    {
        foreach (var kw in SqlKeywords)
        {
            if (allowed.Contains(kw) && MatchesPrefix(kw, prefix) && seen.Add(kw))
                results.Add(new SqlCompletionData(kw, CompletionCategory.Keyword));
        }
    }

    private List<(string schemaTable, string? alias)> ParseFromTables(string text, int caretOffset)
    {
        var results = new List<(string, string?)>();
        var searchText = caretOffset < text.Length ? text[..caretOffset] : text;

        foreach (Match m in FromJoinRegex.Matches(searchText))
        {
            var schema = m.Groups[1].Success ? m.Groups[1].Value : "dbo";
            var table = m.Groups[2].Value;
            var alias = m.Groups[3].Success ? m.Groups[3].Value : null;

            // Skip SQL keywords that might be mismatched (e.g. FROM INNER -> "INNER" is not a table)
            if (IsKeyword(table)) continue;

            var schemaTable = $"{schema}.{table}";
            results.Add((schemaTable, alias));
        }

        return results;
    }

    private static bool IsKeyword(string word)
    {
        foreach (var kw in SqlKeywords)
        {
            if (kw.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static (string word, int startOffset) GetWordBeforeCaret(string text, int offset)
    {
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        return (text[start..offset], start);
    }

    private static bool MatchesPrefix(string text, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return true;
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

}
