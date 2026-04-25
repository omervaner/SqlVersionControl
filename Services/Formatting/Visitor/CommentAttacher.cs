using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlVersionControl.Services.Formatting.Visitor;

/// <summary>
/// Walks ScriptDom's ScriptTokenStream, classifies each comment token against the four
/// attachment cases (leading block / leading same-line / trailing same-line / interior),
/// and assigns each to a target fragment. Placeholder in 4a — 4g fills in the real
/// algorithm from docs/FORMATTER-OVERHAUL.md § "Comment Attachment Rules".
/// </summary>
internal sealed record CommentInfo(
    IReadOnlyList<string> Leading,
    IReadOnlyList<string> Trailing,
    IReadOnlyList<string> Interior)
{
    public static CommentInfo Empty { get; } =
        new(System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>());
}

internal static class CommentAttacher
{
    public static IReadOnlyDictionary<TSqlFragment, CommentInfo> Attach(TSqlFragment fragment)
    {
        // 4a: intentionally empty map. 4g replaces this with the four-case algorithm.
        return new Dictionary<TSqlFragment, CommentInfo>();
    }
}
