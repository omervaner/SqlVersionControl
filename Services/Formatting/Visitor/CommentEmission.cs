using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlVersionControl.Services.Formatting.Visitor;

/// <summary>
/// Hook points invoked by <see cref="TSqlFormatterVisitor"/> at fragment boundaries.
/// No-op in 4a; 4g fills in real emission using <see cref="CommentAttacher"/>'s assignments.
/// </summary>
internal static class CommentEmission
{
    public static void EmitLeadingCommentsFor(
        SqlEmitter emitter,
        IReadOnlyDictionary<TSqlFragment, CommentInfo> attachments,
        TSqlFragment fragment)
    {
        // 4a no-op. 4g: look up attachments[fragment].Leading and emit each with indent.
    }

    public static void EmitTrailingCommentsFor(
        SqlEmitter emitter,
        IReadOnlyDictionary<TSqlFragment, CommentInfo> attachments,
        TSqlFragment fragment)
    {
        // 4a no-op. 4g: look up attachments[fragment].Trailing and emit inline.
    }
}
