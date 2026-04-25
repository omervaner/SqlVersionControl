using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting.Visitor;

namespace SqlVersionControl.Services.Formatting;

/// <summary>
/// ScriptDom-based formatter. Parse via TSql170Parser (inside a 2-second Task.Wait timeout),
/// run the visitor, emit. Parse-all-fail or visitor-throw falls back to the legacy formatter
/// (the dispatcher surfaces FallbackOccurred when the toggle gate allows). See
/// docs/FORMATTER-INTERNALS.md for visitor details.
/// </summary>
public static class ScriptDomFormatter
{
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(2);

    public static string Format(string sql, FormatterOptions options)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        var prepared = SqlPreRepair.Normalize(sql);

        TSqlFragment? fragment = null;
        bool parsed = false;

        var parseTask = Task.Run(() =>
        {
            parsed = SelectionParseStaircase.TryParse(prepared, out fragment, out _);
        });

        if (!parseTask.Wait(ParseTimeout))
        {
            // Parse hung — return input unchanged. Legacy on the same input would also be slow.
            return sql;
        }

        if (!parsed || fragment is null)
        {
            return LegacyHogimnFormatter.Format(sql);
        }

        try
        {
            var emitter = new SqlEmitter(options);
            var attachments = fragment is TSqlScript script
                ? CommentAttacher.Attach(script)
                : (IReadOnlyDictionary<TSqlFragment, CommentInfo>)new Dictionary<TSqlFragment, CommentInfo>();
            var visitor = new TSqlFormatterVisitor(emitter, options, attachments);
            if (fragment is TSqlScript or TSqlBatch)
            {
                fragment.Accept(visitor);
            }
            else
            {
                visitor.EmitFragmentDefault(fragment);
            }
            return emitter.ToString();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ScriptDomFormatter.Visit", ex);
            return LegacyHogimnFormatter.Format(sql);
        }
    }
}
