using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlVersionControl.Services.Formatting.Visitor;

/// <summary>
/// Pre-parse repair pass for known-benign quirks the engine tolerates but ScriptDom rejects.
/// Strict whitelist — must not mangle valid SQL. Unrepairable inputs are returned unchanged so
/// the staircase + legacy fallback handle them. See docs/FORMATTER-INTERNALS.md § 4c-ii.
/// </summary>
internal static class SqlPreRepair
{
    private static readonly Regex CteIntroductionAtLineStart = new(
        @"^(?<ws>[ \t]*)WITH\s+\[?\w+\]?\s*(\([^)]*\))?\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;
        return RepairMissingSemicolonBeforeWith(sql);
    }

    private static string RepairMissingSemicolonBeforeWith(string sql)
    {
        var lines = SplitLines(sql);
        var sb = new StringBuilder(sql.Length + 16);

        for (int i = 0; i < lines.Count; i++)
        {
            var (content, eol) = lines[i];
            var match = CteIntroductionAtLineStart.Match(content);
            if (match.Success && PriorStatementIsUnterminated(lines, i))
            {
                var ws = match.Groups["ws"].Value;
                sb.Append(ws);
                sb.Append(';');
                sb.Append(content, ws.Length, content.Length - ws.Length);
            }
            else
            {
                sb.Append(content);
            }
            sb.Append(eol);
        }

        return sb.ToString();
    }

    private static bool PriorStatementIsUnterminated(List<(string Content, string Eol)> lines, int currentIndex)
    {
        for (int i = currentIndex - 1; i >= 0; i--)
        {
            var stripped = StripTrailingLineComment(lines[i].Content).Trim();
            if (stripped.Length == 0) continue;

            if (stripped.EndsWith(";", StringComparison.Ordinal)) return false;
            if (string.Equals(stripped, "GO", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
        return false;
    }

    private static string StripTrailingLineComment(string s)
    {
        int idx = s.IndexOf("--", StringComparison.Ordinal);
        return idx < 0 ? s : s.Substring(0, idx);
    }

    private static List<(string Content, string Eol)> SplitLines(string s)
    {
        var result = new List<(string, string)>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;

            string content;
            string eol;
            if (i > 0 && s[i - 1] == '\r')
            {
                content = s.Substring(start, i - 1 - start);
                eol = "\r\n";
            }
            else
            {
                content = s.Substring(start, i - start);
                eol = "\n";
            }
            result.Add((content, eol));
            start = i + 1;
        }
        if (start <= s.Length - 1 || start == 0)
        {
            result.Add((s.Substring(start), string.Empty));
        }
        else if (start == s.Length)
        {
            // Trailing newline already consumed; nothing to add.
        }
        return result;
    }
}
