using System.Xml.Linq;

namespace SqlVersionControl.Services;

/// <summary>
/// Extracts statement character offsets from raw plan XML.
/// SQL Server's StmtSimple elements carry StatementStartOffset / StatementEndOffset
/// as byte positions into the NVARCHAR proc definition (2 bytes per char).
/// </summary>
public static class PlanXmlHelper
{
    public static Dictionary<string, (int StartChar, int EndChar)> ExtractStatementOffsets(string rawXml)
    {
        var result = new Dictionary<string, (int, int)>();
        try
        {
            var doc = XDocument.Parse(rawXml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            foreach (var stmt in doc.Descendants(ns + "StmtSimple"))
            {
                var text = stmt.Attribute("StatementText")?.Value;
                var startBytes = ParseLong(stmt.Attribute("StatementStartOffset")?.Value);
                var endBytes = ParseLong(stmt.Attribute("StatementEndOffset")?.Value);

                if (string.IsNullOrEmpty(text) || startBytes < 0 || endBytes <= 0)
                    continue;

                result.TryAdd(text, ((int)(startBytes / 2), (int)(endBytes / 2)));
            }
        }
        catch { /* parse errors — highlighting just won't work */ }
        return result;
    }

    private static long ParseLong(string? value)
        => long.TryParse(value, out var v) ? v : -1;
}
