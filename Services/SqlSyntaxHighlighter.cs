using System.Text.RegularExpressions;
using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

public static class SqlSyntaxHighlighter
{
    // SQL Keywords
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // DDL
        "CREATE", "ALTER", "DROP", "TRUNCATE", "TABLE", "VIEW", "INDEX", "PROCEDURE", "PROC",
        "FUNCTION", "TRIGGER", "SCHEMA", "DATABASE", "CONSTRAINT", "PRIMARY", "FOREIGN", "KEY",
        "REFERENCES", "UNIQUE", "CHECK", "DEFAULT", "IDENTITY", "CLUSTERED", "NONCLUSTERED",

        // DML
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "INTO", "VALUES", "FROM", "WHERE",
        "SET", "OUTPUT", "INSERTED", "DELETED",

        // Clauses
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "AND", "OR", "NOT",
        "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL", "AS", "CASE", "WHEN", "THEN", "ELSE", "END",
        "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC", "TOP", "DISTINCT", "ALL", "UNION",
        "INTERSECT", "EXCEPT", "WITH", "OVER", "PARTITION", "ROW_NUMBER", "RANK", "DENSE_RANK",

        // Control flow
        "IF", "ELSE", "BEGIN", "END", "WHILE", "BREAK", "CONTINUE", "RETURN", "GOTO", "WAITFOR",
        "TRY", "CATCH", "THROW", "RAISERROR",

        // Transactions
        "TRANSACTION", "TRAN", "COMMIT", "ROLLBACK", "SAVE", "SAVEPOINT",

        // Variables and declarations
        "DECLARE", "SET", "EXEC", "EXECUTE", "PRINT", "USE", "GO",

        // Data types
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BIT", "DECIMAL", "NUMERIC", "MONEY", "SMALLMONEY",
        "FLOAT", "REAL", "DATETIME", "DATETIME2", "DATE", "TIME", "SMALLDATETIME", "DATETIMEOFFSET",
        "CHAR", "VARCHAR", "TEXT", "NCHAR", "NVARCHAR", "NTEXT", "BINARY", "VARBINARY", "IMAGE",
        "UNIQUEIDENTIFIER", "XML", "CURSOR", "TABLE", "SQL_VARIANT", "TIMESTAMP", "ROWVERSION",

        // Other
        "NOCOUNT", "NOLOCK", "ROWLOCK", "TABLOCK", "HOLDLOCK", "UPDLOCK", "XLOCK",
        "CURSOR", "FETCH", "NEXT", "PRIOR", "FIRST", "LAST", "ABSOLUTE", "RELATIVE",
        "OPEN", "CLOSE", "DEALLOCATE", "FOR", "READONLY", "OUTPUT", "OUT", "MAX"
    };

    // Regex patterns for different token types
    private static readonly Regex CommentBlockRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CommentLineRegex = new(@"--.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex StringRegex = new(@"'(?:[^']|'')*'|N'(?:[^']|'')*'", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+\.?\d*\b", RegexOptions.Compiled);
    private static readonly Regex IdentifierRegex = new(@"\[.+?\]", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
    private static readonly Regex VariableRegex = new(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex SystemFunctionRegex = new(@"@@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static List<HighlightedSegment> Highlight(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<HighlightedSegment> { new() { Text = "", TokenType = SqlTokenType.Default } };

        var tokens = new List<(int Start, int Length, SqlTokenType Type)>();

        // Find all block comments
        foreach (Match m in CommentBlockRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.Comment));

        // Find all line comments
        foreach (Match m in CommentLineRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.Comment));

        // Find all strings
        foreach (Match m in StringRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.String));

        // Find system functions (@@)
        foreach (Match m in SystemFunctionRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.SystemFunction));

        // Find variables (@)
        foreach (Match m in VariableRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.Variable));

        // Find bracketed identifiers
        foreach (Match m in IdentifierRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.Identifier));

        // Find numbers
        foreach (Match m in NumberRegex.Matches(text))
            tokens.Add((m.Index, m.Length, SqlTokenType.Number));

        // Find words (keywords or identifiers)
        foreach (Match m in WordRegex.Matches(text))
        {
            var type = Keywords.Contains(m.Value) ? SqlTokenType.Keyword : SqlTokenType.Default;
            tokens.Add((m.Index, m.Length, type));
        }

        // Remove overlapping tokens (comments and strings take precedence)
        tokens = RemoveOverlaps(tokens);

        // Sort by position
        tokens = tokens.OrderBy(t => t.Start).ToList();

        // Build segments
        return BuildSegments(text, tokens);
    }

    private static List<(int Start, int Length, SqlTokenType Type)> RemoveOverlaps(
        List<(int Start, int Length, SqlTokenType Type)> tokens)
    {
        // Priority: Comments > Strings > SystemFunctions > Variables > Others
        var priorityOrder = new Dictionary<SqlTokenType, int>
        {
            { SqlTokenType.Comment, 0 },
            { SqlTokenType.String, 1 },
            { SqlTokenType.SystemFunction, 2 },
            { SqlTokenType.Variable, 3 },
            { SqlTokenType.Identifier, 4 },
            { SqlTokenType.Keyword, 5 },
            { SqlTokenType.Number, 6 },
            { SqlTokenType.Default, 7 }
        };

        var sorted = tokens.OrderBy(t => priorityOrder[t.Type]).ThenBy(t => t.Start).ToList();
        var result = new List<(int Start, int Length, SqlTokenType Type)>();
        var covered = new List<(int Start, int End)>();

        foreach (var token in sorted)
        {
            var tokenEnd = token.Start + token.Length;
            var overlaps = covered.Any(c => !(tokenEnd <= c.Start || token.Start >= c.End));

            if (!overlaps)
            {
                result.Add(token);
                covered.Add((token.Start, tokenEnd));
            }
        }

        return result;
    }

    private static List<HighlightedSegment> BuildSegments(
        string text,
        List<(int Start, int Length, SqlTokenType Type)> tokens)
    {
        var segments = new List<HighlightedSegment>();
        var pos = 0;

        foreach (var token in tokens)
        {
            // Add any text before this token as default
            if (token.Start > pos)
            {
                segments.Add(new HighlightedSegment
                {
                    Text = text.Substring(pos, token.Start - pos),
                    TokenType = SqlTokenType.Default
                });
            }

            // Add the token
            segments.Add(new HighlightedSegment
            {
                Text = text.Substring(token.Start, token.Length),
                TokenType = token.Type
            });

            pos = token.Start + token.Length;
        }

        // Add any remaining text
        if (pos < text.Length)
        {
            segments.Add(new HighlightedSegment
            {
                Text = text.Substring(pos),
                TokenType = SqlTokenType.Default
            });
        }

        // Ensure at least one segment
        if (segments.Count == 0)
        {
            segments.Add(new HighlightedSegment { Text = text, TokenType = SqlTokenType.Default });
        }

        return segments;
    }
}
