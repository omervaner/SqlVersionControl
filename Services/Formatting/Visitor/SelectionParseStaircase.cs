using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlVersionControl.Services.Formatting.Visitor;

/// <summary>
/// Three-step parse staircase for selection-mid-statement: TSqlScript → ParseStatementList →
/// ParseExpression. Returns the first success. Callers route all-fail to LegacyHogimnFormatter.
/// See docs/FORMATTER-OVERHAUL.md § "Selection-Mid-Statement Corner Case". (4A-PLAN.md says
/// ParseStatement — the actual ScriptDom API name is ParseStatementList, same intent.)
/// </summary>
internal static class SelectionParseStaircase
{
    public static bool TryParse(string sql, out TSqlFragment? fragment, out IList<ParseError> errors)
    {
        errors = new List<ParseError>();
        fragment = null;

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);

        using (var reader = new StringReader(sql))
        {
            var result = parser.Parse(reader, out var scriptErrors);
            if (scriptErrors == null || scriptErrors.Count == 0)
            {
                fragment = result;
                return true;
            }
            errors = scriptErrors;
        }

        using (var reader = new StringReader(sql))
        {
            var stmt = parser.ParseStatementList(reader, out var stmtErrors);
            if (stmt != null && (stmtErrors == null || stmtErrors.Count == 0))
            {
                fragment = stmt;
                errors = stmtErrors ?? new List<ParseError>();
                return true;
            }
        }

        using (var reader = new StringReader(sql))
        {
            var expr = parser.ParseExpression(reader, out var exprErrors);
            if (expr != null && (exprErrors == null || exprErrors.Count == 0))
            {
                fragment = expr;
                errors = exprErrors ?? new List<ParseError>();
                return true;
            }
        }

        return false;
    }
}
