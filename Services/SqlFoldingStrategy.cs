using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace SqlVersionControl.Services;

public class SqlFoldingStrategy
{
    public IEnumerable<NewFolding> CreateNewFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var text = document.Text;

        FoldBeginEnd(text, foldings);
        FoldBlockComments(text, foldings);

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    private static void FoldBeginEnd(string text, List<NewFolding> foldings)
    {
        var stack = new Stack<int>();
        var i = 0;
        while (i < text.Length)
        {
            // Skip strings
            if (text[i] == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'') i++;
                i++;
                continue;
            }
            // Skip line comments
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            // Skip block comments
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i += 2;
                continue;
            }

            if (IsKeywordAt(text, i, "BEGIN"))
            {
                // Skip BEGIN TRAN / BEGIN TRANSACTION (no matching END)
                var afterBegin = i + 5;
                var rest = SkipWhitespace(text, afterBegin);
                if (!IsKeywordAt(text, rest, "TRAN") && !IsKeywordAt(text, rest, "TRANSACTION"))
                    stack.Push(i);
                i += 5;
                continue;
            }

            if (IsKeywordAt(text, i, "END"))
            {
                if (stack.Count > 0)
                {
                    var startOffset = stack.Pop();
                    foldings.Add(new NewFolding(startOffset, i + 3) { Name = "BEGIN...END" });
                }
                i += 3;
                continue;
            }

            i++;
        }
    }

    private static void FoldBlockComments(string text, List<NewFolding> foldings)
    {
        var i = 0;
        while (i < text.Length - 1)
        {
            if (text[i] == '/' && text[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++;
                if (i < text.Length - 1)
                {
                    i += 2;
                    if (text[start..i].Contains('\n'))
                        foldings.Add(new NewFolding(start, i) { Name = "/* ... */" });
                }
            }
            else i++;
        }
    }

    private static bool IsKeywordAt(string text, int pos, string keyword)
    {
        if (pos + keyword.Length > text.Length) return false;
        if (pos > 0 && char.IsLetterOrDigit(text[pos - 1])) return false;
        if (!text.AsSpan(pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        var after = pos + keyword.Length;
        if (after < text.Length && char.IsLetterOrDigit(text[after])) return false;
        return true;
    }

    private static int SkipWhitespace(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        return pos;
    }
}
