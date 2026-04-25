using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlVersionControl.Services.Formatting.Visitor;

/// <summary>
/// Visitor for the new formatter. 4a overrode only script- and batch-level entry points;
/// 4b-i adds <c>SelectStatement</c> and <c>QuerySpecification</c> to wire clause keywords
/// through <see cref="SqlEmitter.BeginClauseScope"/> so SELECT / FROM / WHERE / GROUP BY /
/// HAVING / ORDER BY right-align. Clause bodies and anything without a dedicated override
/// continue to route through the 4a scaffold (<see cref="EmitFragmentDefault"/> via
/// <see cref="Sql170ScriptGenerator"/>) until later slices replace them.
/// </summary>
internal sealed class TSqlFormatterVisitor : TSqlFragmentVisitor
{
    private readonly SqlEmitter _emitter;
    private readonly FormatterOptions _options;
    private readonly IReadOnlyDictionary<TSqlFragment, CommentInfo> _attachments;
    private readonly SqlScriptGenerator _generator;

    // 4b-iv: CASE-specific nesting depth. Drives the WHEN-pad and END-pad calculations
    // independently from the global _indentLevel (which the surrounding subquery / scope can
    // inflate). depth=1 = top-level CASE (END at CASE column); depth=2 = nested CASE inside
    // outer's THEN body (END at outer's WHEN/ELSE column).
    private int _caseDepth;

    public TSqlFormatterVisitor(
        SqlEmitter emitter,
        FormatterOptions options,
        IReadOnlyDictionary<TSqlFragment, CommentInfo> attachments)
    {
        _emitter = emitter;
        _options = options;
        _attachments = attachments;
        _generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = options.Uppercase ? KeywordCasing.Uppercase : KeywordCasing.Lowercase,
            IndentationSize = options.IndentSize,
            IncludeSemicolons = options.IncludeSemicolons,
            SqlVersion = SqlVersion.Sql170,
            AlignClauseBodies = true,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeGroupByClause = true,
            NewLineBeforeHavingClause = true,
            NewLineBeforeOrderByClause = true,
        });
    }

    public override void ExplicitVisit(TSqlScript script)
    {
        for (int i = 0; i < script.Batches.Count; i++)
        {
            if (i > 0)
            {
                _emitter.WriteLine();
                _emitter.WriteKeyword("GO");
                _emitter.NewLine();
            }
            script.Batches[i].Accept(this);
        }
    }

    public override void ExplicitVisit(TSqlBatch batch)
    {
        for (int i = 0; i < batch.Statements.Count; i++)
        {
            if (i > 0) _emitter.NewLine();
            var stmt = batch.Statements[i];
            CommentEmission.EmitLeadingCommentsFor(_emitter, _attachments, stmt);
            EmitFragmentDefault(stmt);
            CommentEmission.EmitTrailingCommentsFor(_emitter, _attachments, stmt);
        }
    }

    public override void ExplicitVisit(SelectStatement statement)
    {
        // SelectStatement-level fallback (4b-ii): if the inner QuerySpec has trip-flags we
        // can't handle yet, emit the whole SelectStatement via the generator. Reason —
        // Sql170ScriptGenerator on a *bare* QuerySpec drops trailing clauses (WHERE / GROUP BY
        // / HAVING / ORDER BY) when JOINs are present; calling it on the surrounding
        // SelectStatement produces full output. Picked up by 4b-iii.
        if (statement.QueryExpression is QuerySpecification qsBail && QuerySpecRequiresFallback(qsBail))
        {
            EmitGeneratorRaw(statement);                                   // bypass EmitFragmentDefault routing (would re-enter and stack-overflow)
            return;
        }

        if (statement.WithCtesAndXmlNamespaces != null)
        {
            statement.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        if (statement.QueryExpression is QuerySpecification qs)
        {
            qs.Accept(this);
        }
        else if (statement.QueryExpression != null)
        {
            EmitFragmentDefault(statement.QueryExpression);
        }

        if (statement.Into != null || statement.On != null
            || (statement.ComputeClauses != null && statement.ComputeClauses.Count > 0)
            || (statement.OptimizerHints != null && statement.OptimizerHints.Count > 0))
        {
            // Niche SELECT features not covered in 4b-i; fall through to generator for the whole statement.
            _emitter.NewLine();
            EmitFragmentDefault(statement);
        }
    }

    private static bool QuerySpecRequiresFallback(QuerySpecification q)
    {
        // 4b-iii-a: hasJoins and hasMultiTableFromClause guards removed. JOIN subtypes route
        // via EmitTableReferenceBody; old-style implicit joins (FROM a, b) are emitted as a
        // comma-joined list of TableReferences in the FROM body. Remaining trip-flags
        // (TopRowFilter / OffsetClause / ForClause) stay until their own slices — they can't
        // be expressed correctly by the current visitor coverage.
        return q.TopRowFilter != null || q.OffsetClause != null || q.ForClause != null;
    }

    public override void ExplicitVisit(QuerySpecification q)
    {
        // 4b-i niche-feature fallback: still needed when QuerySpec is reached as a subquery
        // (not via SelectStatement, which has its own fallback above). For top-level
        // SelectStatements, SelectStatement's fallback fires first and we never get here.
        // Caveat: subquery-with-JOINs still drops trailing clauses (ScriptDom bare-QuerySpec
        // quirk) — known limitation, picked up by 4b-iii.
        if (QuerySpecRequiresFallback(q))
        {
            EmitFragmentDefault(q);
            return;
        }

        using (_emitter.BeginClauseScope())
        {
            // SELECT
            _emitter.WriteClauseKeyword("SELECT");
            if (q.UniqueRowFilter == UniqueRowFilter.Distinct) _emitter.Write("DISTINCT ");
            else if (q.UniqueRowFilter == UniqueRowFilter.All) _emitter.Write("ALL ");
            EmitWrappedList(q.SelectElements, RenderSelectElementForMeasure, EmitSelectElementBody);
            _emitter.NewLine();

            if (q.FromClause != null)
            {
                _emitter.WriteClauseKeyword("FROM");
                // Old-style implicit joins (FROM a, b, c) wrap when long. Modern JOIN trees
                // always have a single outer TableReference (the top of the left-associative
                // chain), so list-of-one bypasses the wrap helper entirely.
                EmitWrappedList(q.FromClause.TableReferences, RenderTableReferenceForMeasure, EmitTableReferenceBody);
                _emitter.NewLine();
            }

            if (q.WhereClause != null)
            {
                _emitter.WriteClauseKeyword("WHERE");
                EmitSearchConditionBody(q.WhereClause.SearchCondition);
                _emitter.NewLine();
            }

            if (q.GroupByClause != null)
            {
                _emitter.WriteClauseKeyword("GROUP BY");
                EmitWrappedList(q.GroupByClause.GroupingSpecifications, RenderGroupingSpecificationForMeasure, EmitGroupingSpecificationBody);
                _emitter.NewLine();
            }

            if (q.HavingClause != null)
            {
                _emitter.WriteClauseKeyword("HAVING");
                EmitSearchConditionBody(q.HavingClause.SearchCondition);
                _emitter.NewLine();
            }

            if (q.OrderByClause != null)
            {
                _emitter.WriteClauseKeyword("ORDER BY");
                EmitWrappedList(q.OrderByClause.OrderByElements, RenderOrderByElementForMeasure, EmitOrderByElementBody);
                _emitter.NewLine();
            }
        }
    }

    public override void ExplicitVisit(ScalarSubquery sub)
    {
        // 4b-ii: subquery-in-expression breaks to indented block. We don't open our own
        // BeginClauseScope here — Accept dispatches to ExplicitVisit(QuerySpecification),
        // which opens its own scope. Opening one here too would double-wrap and double-indent.
        // 4e-ii: AtLineStart guard before `)` — same pattern as CommonTableExpression. When
        // called at top level (no parent clause scope), inner QuerySpec's flush already
        // wrote a trailing NL; an unconditional NewLine here adds a blank line. AtLineStart
        // returns false inside any clause scope, so the in-scope path is unchanged.
        _emitter.Write("(");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitSubqueryQueryExpression(sub.QueryExpression);
        }
        if (!_emitter.AtLineStart) _emitter.NewLine();
        _emitter.Write(")");
    }

    public override void ExplicitVisit(InPredicate p)
    {
        // 4b-ii: only the subquery shape gets break-to-block. Values-list IN (a, b, c) stays
        // inline via generator (4b-iii or later may revisit). 4e-ii: AtLineStart guard, see
        // ScalarSubquery comment.
        if (p.Subquery == null) { EmitFragmentDefault(p); return; }
        EmitExpressionScaffold(p.Expression);                              // LHS — temporary scaffold, dies in 4f
        _emitter.Write(p.NotDefined ? " NOT IN (" : " IN (");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitSubqueryQueryExpression(p.Subquery.QueryExpression);
        }
        if (!_emitter.AtLineStart) _emitter.NewLine();
        _emitter.Write(")");
    }

    public override void ExplicitVisit(ExistsPredicate p)
    {
        // 4e-ii: AtLineStart guard, see ScalarSubquery comment.
        _emitter.Write("EXISTS (");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitSubqueryQueryExpression(p.Subquery.QueryExpression);
        }
        if (!_emitter.AtLineStart) _emitter.NewLine();
        _emitter.Write(")");
    }

    // 4b-ii: subquery body dispatcher. QuerySpecification opens its own BeginClauseScope so
    // we just Accept. 4b-iv: BinaryQueryExpression (UNION / INTERSECT / EXCEPT) dispatches
    // through its override — also handles chained UNIONs in arm position (D8).
    private void EmitSubqueryQueryExpression(QueryExpression qe)
    {
        if (qe is QuerySpecification qs) { qs.Accept(this); return; }
        if (qe is BinaryQueryExpression bqe) { ExplicitVisit(bqe); return; }
        EmitFragmentDefault(qe);
    }

    // 4b-iii-a: per-TableReference dispatcher for FROM body. Each step adds a routing branch;
    // unhandled variants (NamedTableReference, VariableTableReference, etc.) fall through to a
    // single-line generator render — safe because bare-QuerySpec generation's trailing-clause
    // drop is specific to QuerySpec, not TableReference.
    private void EmitTableReferenceBody(TableReference tableRef)
    {
        if (tableRef is QualifiedJoin qj) { ExplicitVisit(qj); return; }
        if (tableRef is UnqualifiedJoin uj) { ExplicitVisit(uj); return; }
        if (tableRef is QueryDerivedTable qdt) { ExplicitVisit(qdt); return; }
        _generator.GenerateScript(tableRef, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    public override void ExplicitVisit(QualifiedJoin qj)
    {
        // 4b-iii-a: stacked JOINs. Recurse into FirstTableReference (may itself be a QualifiedJoin
        // for chained joins — ScriptDom parses `a JOIN b JOIN c` left-associatively), then emit
        // the current JOIN on its own body-continuation line in the outer FROM scope.
        EmitTableReferenceBody(qj.FirstTableReference);
        _emitter.NewLine();
        _emitter.Write(QualifiedJoinKeyword(qj.QualifiedJoinType) + " ");
        EmitTableReferenceBody(qj.SecondTableReference);
        if (qj.SearchCondition != null)
        {
            _generator.GenerateScript(qj.SearchCondition, out var condText);
            var cond = (condText ?? string.Empty).Trim();
            // Inline ON by default; break to own line only if the rendered condition is long
            // enough that inline would exceed MaxLineLength. Heuristic uses condition length vs
            // roughly 2/3 of MaxLineLength — a precise check would need to know the flush-time
            // outer body column, which depends on the not-yet-finalised maxKw. Good enough for
            // realistic sprocs; if the corpus shows false-negatives, tighten later.
            if (cond.Length > _options.MaxLineLength * 2 / 3)
            {
                _emitter.NewLine();
                var padUnderJoin = new string(' ', _options.IndentSize);
                _emitter.Write(padUnderJoin + "ON " + cond);
            }
            else
            {
                _emitter.Write(" ON " + cond);
            }
        }
    }

    private static string QualifiedJoinKeyword(QualifiedJoinType t) => t switch
    {
        QualifiedJoinType.Inner => "INNER JOIN",
        QualifiedJoinType.LeftOuter => "LEFT OUTER JOIN",
        QualifiedJoinType.RightOuter => "RIGHT OUTER JOIN",
        QualifiedJoinType.FullOuter => "FULL OUTER JOIN",
        _ => "INNER JOIN",
    };

    public override void ExplicitVisit(UnqualifiedJoin uj)
    {
        // 4b-iii-a: CROSS JOIN / CROSS APPLY / OUTER APPLY. Same stacking pattern as QualifiedJoin
        // but no ON clause. APPLY variants' SecondTableReference is typically a QueryDerivedTable
        // which dispatches back through EmitTableReferenceBody once that override lands.
        EmitTableReferenceBody(uj.FirstTableReference);
        _emitter.NewLine();
        _emitter.Write(UnqualifiedJoinKeyword(uj.UnqualifiedJoinType) + " ");
        EmitTableReferenceBody(uj.SecondTableReference);
    }

    private static string UnqualifiedJoinKeyword(UnqualifiedJoinType t) => t switch
    {
        UnqualifiedJoinType.CrossJoin => "CROSS JOIN",
        UnqualifiedJoinType.CrossApply => "CROSS APPLY",
        UnqualifiedJoinType.OuterApply => "OUTER APPLY",
        _ => "CROSS JOIN",
    };

    public override void ExplicitVisit(QueryDerivedTable qdt)
    {
        // 4b-iii-a: subquery-in-FROM. Same break-to-block pattern as ScalarSubquery (4b-ii).
        // Don't open our own BeginClauseScope — the inner QuerySpecification.Accept opens one.
        _emitter.Write("(");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitSubqueryQueryExpression(qdt.QueryExpression);
        }
        _emitter.NewLine();
        _emitter.Write(")");
        if (qdt.Alias != null)
        {
            _generator.GenerateScript(qdt.Alias, out var aliasText);
            _emitter.Write(" AS " + (aliasText ?? string.Empty).Trim());
        }
    }

    public override void ExplicitVisit(CommonTableExpression cte)
    {
        // 4b-iii-b: a single CTE — `name AS (\n    <body>\n)`. Flat indent per D2 (no own
        // clause scope). Inner QuerySpec opens its own BeginClauseScope with captured
        // _indentLevel = 1 (after our Indent()), so inner SELECT lands at col 4.
        _generator.GenerateScript(cte.ExpressionName, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        if (cte.Columns != null && cte.Columns.Count > 0)
        {
            _emitter.Write(" (");
            for (int i = 0; i < cte.Columns.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(cte.Columns[i], out var colText);
                _emitter.Write((colText ?? string.Empty).Trim());
            }
            _emitter.Write(")");
        }

        _emitter.Write(" AS (");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitSubqueryQueryExpression(cte.QueryExpression);
        }
        // Inner QuerySpec's trailing NewLine survives when the clause scope flushes to _sb
        // (CTE emits outside any clause scope, so EndClauseScope doesn't TrimEnd before
        // injecting — cf. ScalarSubquery which always pops into a parent scope). Skip the
        // explicit NewLine when we're already at line start.
        if (!_emitter.AtLineStart) _emitter.NewLine();
        _emitter.Write(")");
    }

    public override void ExplicitVisit(BooleanBinaryExpression bbe)
    {
        // 4b-iv (D3): inside a clause scope (WHERE / HAVING root), AND / OR right-align with the
        // clause keyword via WriteClauseKeyword. Outside a clause scope (ON, CASE WHEN condition,
        // arbitrary scalar context), fall back to a generator render — preserves current behavior
        // and keeps the AND/OR-in-ON quirk narrow rather than redesigning ON's body model. Recursion
        // routes each operand through EmitSearchConditionBody, which dispatches subquery shapes
        // through the visitor and other BBEs back here. Termination: operand !is BooleanBinaryExpression
        // hits a terminal branch (subquery breaks to block, or single-line generator render).
        // Test #14 (Format_DeeplyNestedAndOr_TerminatesWithoutStackOverflow) is the ground-truth
        // check — paper reasoning isn't enough here.
        if (!_emitter.InClauseScope)
        {
            EmitGeneratorRaw(bbe);
            return;
        }
        EmitSearchConditionBody(bbe.FirstExpression);
        _emitter.NewLine();
        _emitter.WriteClauseKeyword(bbe.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR");
        EmitSearchConditionBody(bbe.SecondExpression);
    }

    public override void ExplicitVisit(BinaryQueryExpression bqe)
    {
        // 4b-iv (D2): set-operator stacks at statement indent — no clause scope. Each arm is a
        // QueryExpression (typically QuerySpecification, possibly an inner BinaryQueryExpression
        // for chained UNIONs — D8 handles via natural recursion). Recurse into FirstQueryExpression
        // *before* emitting the operator (otherwise reading order is wrong — see Risks).
        EmitSubqueryQueryExpression(bqe.FirstQueryExpression);
        // Top-level: arm1's QuerySpec scope flushed and left _sb at line start — skip the NL.
        // Inside a parent clause scope: the inner buffer was injected without a trailing NL — emit one.
        if (!_emitter.AtLineStart) _emitter.NewLine();
        _emitter.WriteKeyword(BinaryQueryOperatorText(bqe.BinaryQueryExpressionType, bqe.All));
        _emitter.NewLine();
        EmitSubqueryQueryExpression(bqe.SecondQueryExpression);

        // 4b-iv (D4): the top-level ORDER BY on a UNION is on QueryExpression (BinaryQueryExpression
        // inherits it), not on SelectStatement. Emit it in its own one-clause scope at the
        // statement indent — symmetric with QuerySpec's own ORDER BY handling.
        if (bqe.OrderByClause != null)
        {
            if (!_emitter.AtLineStart) _emitter.NewLine();
            using (_emitter.BeginClauseScope())
            {
                _emitter.WriteClauseKeyword("ORDER BY");
                EmitWrappedList(bqe.OrderByClause.OrderByElements, RenderOrderByElementForMeasure, EmitOrderByElementBody);
                _emitter.NewLine();
            }
        }
    }

    private static string BinaryQueryOperatorText(BinaryQueryExpressionType t, bool all) => t switch
    {
        BinaryQueryExpressionType.Union => all ? "UNION ALL" : "UNION",
        BinaryQueryExpressionType.Intersect => "INTERSECT",
        BinaryQueryExpressionType.Except => "EXCEPT",
        _ => "UNION",
    };

    public override void ExplicitVisit(SimpleCaseExpression sice)
    {
        // 4b-iv (D1): CASE <input_expr> on header line; WHEN/ELSE at +IndentSize; END at CASE column.
        // Differs from searched only in (a) header carries InputExpression and (b) WhenExpression
        // is a ScalarExpression (compared against the input) rather than a BooleanExpression.
        _caseDepth++;
        _emitter.Write("CASE ");
        EmitExpressionScaffold(sice.InputExpression);
        var pad = new string(' ', _caseDepth * _options.IndentSize);
        for (int i = 0; i < sice.WhenClauses.Count; i++)
        {
            _emitter.NewLine();
            _emitter.Write(pad + "WHEN ");
            EmitExpressionScaffold(sice.WhenClauses[i].WhenExpression);
            _emitter.Write(" THEN ");
            EmitExpressionScaffold(sice.WhenClauses[i].ThenExpression);
        }
        if (sice.ElseExpression != null)
        {
            _emitter.NewLine();
            _emitter.Write(pad + "ELSE ");
            EmitExpressionScaffold(sice.ElseExpression);
        }
        _emitter.NewLine();
        // END pad uses _caseDepth-1: top-level CASE (depth=1) → 0 spaces, END at CASE col.
        // Nested CASE (depth=2) → IndentSize spaces, END at outer's WHEN/ELSE col. Independent of
        // the global _indentLevel, which the surrounding subquery/CTE can inflate.
        _emitter.Write(new string(' ', (_caseDepth - 1) * _options.IndentSize) + "END");
        _caseDepth--;
    }

    public override void ExplicitVisit(SearchedCaseExpression sce)
    {
        // 4b-iv (D1): CASE on header line; WHEN/ELSE at +IndentSize; END at CASE column.
        // _caseDepth counts CASE-specific nesting independent of the global _indentLevel
        // (which a surrounding subquery/CTE inflates). Top-level CASE: depth 1, END pad 0;
        // nested CASE: depth 2, END pad IndentSize.
        _caseDepth++;
        _emitter.Write("CASE");
        var pad = new string(' ', _caseDepth * _options.IndentSize);
        for (int i = 0; i < sce.WhenClauses.Count; i++)
        {
            _emitter.NewLine();
            _emitter.Write(pad + "WHEN ");
            EmitInlineBooleanScaffold(sce.WhenClauses[i].WhenExpression);
            _emitter.Write(" THEN ");
            EmitExpressionScaffold(sce.WhenClauses[i].ThenExpression);
        }
        if (sce.ElseExpression != null)
        {
            _emitter.NewLine();
            _emitter.Write(pad + "ELSE ");
            EmitExpressionScaffold(sce.ElseExpression);
        }
        _emitter.NewLine();
        _emitter.Write(new string(' ', (_caseDepth - 1) * _options.IndentSize) + "END");
        _caseDepth--;
    }

    // Generator-rendered BooleanExpression collapsed to a single line. CASE WHEN conditions
    // emit inline regardless of internal AND/OR structure (the multi-line BBE rendering goes
    // away in this slice for the in-clause-scope path; CASE WHEN sits outside that path and
    // wants inline).
    private void EmitInlineBooleanScaffold(BooleanExpression expr)
    {
        _generator.GenerateScript(expr, out var t);
        if (string.IsNullOrEmpty(t)) return;
        var lines = t.TrimEnd('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(trimmed);
        }
        _emitter.Write(sb.ToString());
    }

    public override void ExplicitVisit(WithCtesAndXmlNamespaces w)
    {
        // 4b-iii-b: WITH at current indent (col 0 at statement level). Chained CTEs separated
        // by "," + NewLine — the comma lands on the preceding CTE's closing `)` line
        // (trailing-comma style per D3). CTE names, commas, and closing `)` all sit at the
        // WITH indent level.
        _emitter.Write("WITH ");
        for (int i = 0; i < w.CommonTableExpressions.Count; i++)
        {
            if (i > 0) { _emitter.Write(","); _emitter.NewLine(); }
            w.CommonTableExpressions[i].Accept(this);
        }
    }

    // 4b-ii dispatcher: WHERE / HAVING body. If the search condition is one of the three
    // subquery-bearing shapes at top level, route through the visitor (so the override fires
    // and the subquery breaks to a block). Otherwise fall back to the generator scaffold.
    // AND/OR-mixed cases (BooleanBinaryExpression containing a subquery) are a known
    // limitation — see FORMATTER-INTERNALS.md "Known limitations".
    private void EmitSearchConditionBody(BooleanExpression sc)
    {
        switch (sc)
        {
            case BooleanBinaryExpression bbe:
                ExplicitVisit(bbe);
                return;
            case InPredicate ip when ip.Subquery != null:
                ExplicitVisit(ip);
                return;
            case ExistsPredicate ep:
                ExplicitVisit(ep);
                return;
            case BooleanComparisonExpression bce when ComparisonHasMultilineSide(bce):
                EmitComparisonOperandScaffold(bce.FirstExpression);
                _emitter.Write(" " + ComparisonOperatorText(bce.ComparisonType) + " ");
                EmitComparisonOperandScaffold(bce.SecondExpression);
                return;
            default:
                // 4b-iv (D7): after BBE dispatches above, the remaining default-case fragments
                // (BooleanParenthesisExpression, BooleanNotExpression, LikePredicate, BetweenExpression,
                // BooleanTernaryExpression, plus non-subquery BooleanComparisonExpression / non-
                // subquery InPredicate) all render single-line via generator. Inline emit; if a
                // multi-line case surfaces in the corpus, capture-then-update.
                _generator.GenerateScript(sc, out var t);
                _emitter.Write((t ?? string.Empty).Trim());
                return;
        }
    }

    // 4b-iii-b: measure-only render (generator text) for a SelectElement — used by
    // EmitWrappedList to decide inline vs multi-line. Subquery-bearing elements render
    // multi-line here; EmitWrappedList uses only the first line of each render for its
    // threshold measurement.
    private string RenderSelectElementForMeasure(SelectElement se)
    {
        _generator.GenerateScript(se, out var t);
        return (t ?? string.Empty).Trim();
    }

    private string RenderGroupingSpecificationForMeasure(GroupingSpecification gs)
    {
        _generator.GenerateScript(gs, out var t);
        return (t ?? string.Empty).Trim();
    }

    // 4b-iii-b: per-element emission for GROUP BY. Temporary generator scaffold per element —
    // replaced by per-fragment overrides in 4f when arbitrary ScalarExpression coverage lands.
    private void EmitGroupingSpecificationBody(GroupingSpecification gs)
    {
        _generator.GenerateScript(gs, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    private string RenderOrderByElementForMeasure(ExpressionWithSortOrder e)
    {
        _generator.GenerateScript(e, out var t);
        return (t ?? string.Empty).Trim();
    }

    private void EmitOrderByElementBody(ExpressionWithSortOrder e)
    {
        _generator.GenerateScript(e, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    private string RenderTableReferenceForMeasure(TableReference tr)
    {
        _generator.GenerateScript(tr, out var t);
        return (t ?? string.Empty).Trim();
    }

    // 4b-ii dispatcher: SELECT element. If the element is a top-level ScalarSubquery (wrapped
    // in SelectScalarExpression), dispatch through the visitor override; otherwise generator.
    // 4b-iv: same for CaseExpression — multi-line CASE renders through the visitor.
    private void EmitSelectElementBody(SelectElement se)
    {
        if (se is SelectScalarExpression sse)
        {
            if (sse.Expression is ScalarSubquery sq)
            {
                ExplicitVisit(sq);
                EmitOptionalSelectAlias(sse);
                return;
            }
            if (sse.Expression is CaseExpression ce)
            {
                DispatchCaseExpression(ce);
                EmitOptionalSelectAlias(sse);
                return;
            }
        }
        // Temporary scaffold — generator output for a single SelectElement, dies in 4f.
        _generator.GenerateScript(se, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    private void EmitOptionalSelectAlias(SelectScalarExpression sse)
    {
        if (sse.ColumnName == null) return;
        _generator.GenerateScript(sse.ColumnName, out var aliasText);                       // temporary scaffold, dies in 4f
        _emitter.Write(" AS " + (aliasText ?? string.Empty).Trim());
    }

    private void DispatchCaseExpression(CaseExpression ce)
    {
        if (ce is SearchedCaseExpression sce) { ExplicitVisit(sce); return; }
        if (ce is SimpleCaseExpression sice) { ExplicitVisit(sice); return; }
        EmitGeneratorRaw(ce);
    }

    // Temporary scaffold — dies in 4f when arbitrary ScalarExpression has its own visitor coverage.
    // Used by InPredicate's LHS rendering and as the comparison-operand fallback.
    // 4b-iv: dispatches CaseExpression through the visitor for multi-line CASE inside any
    // expression position (THEN bodies in nested CASE, etc.).
    private void EmitExpressionScaffold(ScalarExpression expr)
    {
        if (expr is CaseExpression ce) { DispatchCaseExpression(ce); return; }
        _generator.GenerateScript(expr, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    // Temporary scaffold — dies in 4f. For BooleanComparisonExpression where one side is a
    // ScalarSubquery: the subquery side dispatches through the visitor (breaks to block); the
    // other side renders inline via generator.
    // 4b-iv: also dispatches CaseExpression so `WHERE CASE … END = 'x'` routes through the
    // override.
    private void EmitComparisonOperandScaffold(ScalarExpression expr)
    {
        if (expr is ScalarSubquery sq) { ExplicitVisit(sq); return; }
        if (expr is CaseExpression ce) { DispatchCaseExpression(ce); return; }
        EmitExpressionScaffold(expr);
    }

    // 4b-iv: a BooleanComparisonExpression dispatches through the visitor (rather than rendering
    // inline via generator) when at least one side is a multi-line shape — ScalarSubquery (4b-ii)
    // or CaseExpression (4b-iv).
    private static bool ComparisonHasMultilineSide(BooleanComparisonExpression bce)
    {
        return bce.FirstExpression is ScalarSubquery || bce.SecondExpression is ScalarSubquery
            || bce.FirstExpression is CaseExpression || bce.SecondExpression is CaseExpression;
    }

    private static string ComparisonOperatorText(BooleanComparisonType t) => t switch
    {
        BooleanComparisonType.Equals => "=",
        BooleanComparisonType.GreaterThan => ">",
        BooleanComparisonType.LessThan => "<",
        BooleanComparisonType.GreaterThanOrEqualTo => ">=",
        BooleanComparisonType.LessThanOrEqualTo => "<=",
        BooleanComparisonType.NotEqualToBrackets => "<>",
        BooleanComparisonType.NotEqualToExclamation => "!=",
        BooleanComparisonType.NotLessThan => "!<",
        BooleanComparisonType.NotGreaterThan => "!>",
        _ => "=",
    };

    public override void ExplicitVisit(InsertStatement stmt)
    {
        // 4c D3: INSERT has no outer clause scope. Shape:
        //   INSERT [INTO] target
        //       (col1, col2, ...)        -- optional, indented +IndentSize
        //   VALUES (...) | SELECT ... | EXEC ...
        //   OUTPUT ...                   -- optional
        var spec = stmt.InsertSpecification;
        if (spec.TopRowFilter != null) { EmitGeneratorRaw(stmt); return; }                    // D6 trip-fallback

        if (stmt.WithCtesAndXmlNamespaces != null)
        {
            stmt.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        _emitter.WriteKeyword("INSERT");
        if (spec.InsertOption == InsertOption.Into) { _emitter.Write(" "); _emitter.WriteKeyword("INTO"); }
        else if (spec.InsertOption == InsertOption.Over) { _emitter.Write(" "); _emitter.WriteKeyword("OVER"); }
        _emitter.Write(" ");
        EmitTableReferenceBody(spec.Target);

        if (spec.Columns != null && spec.Columns.Count > 0)
        {
            _emitter.NewLine();
            using (_emitter.Indent())
            {
                _emitter.Write("(");
                for (int i = 0; i < spec.Columns.Count; i++)
                {
                    if (i > 0) _emitter.Write(", ");
                    _generator.GenerateScript(spec.Columns[i], out var ct);
                    _emitter.Write((ct ?? string.Empty).Trim());
                }
                _emitter.Write(")");
            }
        }

        EmitOutputClause(spec);                                                                // grammar: OUTPUT sits between columns and source

        if (spec.InsertSource != null)
        {
            if (!_emitter.AtLineStart) _emitter.NewLine();
            EmitInsertSource(spec.InsertSource);
        }

        if (!_emitter.AtLineStart) _emitter.NewLine();                                        // statement convention: end on fresh line
    }

    public override void ExplicitVisit(UpdateStatement stmt)
    {
        // 4c D1: UPDATE / SET / FROM / WHERE / OUTPUT right-align in one clause scope.
        var spec = stmt.UpdateSpecification;
        if (spec.TopRowFilter != null) { EmitGeneratorRaw(stmt); return; }

        if (stmt.WithCtesAndXmlNamespaces != null)
        {
            stmt.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        using (_emitter.BeginClauseScope())
        {
            _emitter.WriteClauseKeyword("UPDATE");
            EmitTableReferenceBody(spec.Target);
            _emitter.NewLine();

            _emitter.WriteClauseKeyword("SET");
            EmitWrappedList(spec.SetClauses, RenderSetClauseForMeasure, EmitSetClauseBody);
            _emitter.NewLine();

            EmitOutputClause(spec);                                                            // grammar: OUTPUT before FROM / WHERE

            if (spec.FromClause != null)
            {
                _emitter.WriteClauseKeyword("FROM");
                EmitWrappedList(spec.FromClause.TableReferences, RenderTableReferenceForMeasure, EmitTableReferenceBody);
                _emitter.NewLine();
            }

            if (spec.WhereClause != null)
            {
                _emitter.WriteClauseKeyword("WHERE");
                EmitSearchConditionBody(spec.WhereClause.SearchCondition);
                _emitter.NewLine();
            }
        }
    }

    public override void ExplicitVisit(DeleteStatement stmt)
    {
        // 4c D2: DELETE / FROM / WHERE / OUTPUT in one clause scope.
        // FromClause null → `DELETE FROM <target>` single keyword phrase.
        // FromClause non-null → extended form: `DELETE <target>` + `FROM <fromclause>`.
        var spec = stmt.DeleteSpecification;
        if (spec.TopRowFilter != null) { EmitGeneratorRaw(stmt); return; }

        if (stmt.WithCtesAndXmlNamespaces != null)
        {
            stmt.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        using (_emitter.BeginClauseScope())
        {
            if (spec.FromClause == null)
            {
                // Simple form. Keyword "DELETE" (6 chars); "FROM " goes into body so scope
                // maxKw stays at DELETE vs WHERE (not inflated to "DELETE FROM"=11, which would
                // push WHERE 6 cols further right).
                _emitter.WriteClauseKeyword("DELETE");
                _emitter.Write("FROM ");
                EmitTableReferenceBody(spec.Target);
                _emitter.NewLine();
            }
            else
            {
                // Extended form: DELETE <target> + FROM <join-tree>. DELETE and FROM are separate
                // keyword lines inside the scope — they right-align with WHERE / OUTPUT.
                _emitter.WriteClauseKeyword("DELETE");
                EmitTableReferenceBody(spec.Target);
                _emitter.NewLine();
            }

            EmitOutputClause(spec);                                                            // grammar: OUTPUT before FROM / WHERE

            if (spec.FromClause != null)
            {
                _emitter.WriteClauseKeyword("FROM");
                EmitWrappedList(spec.FromClause.TableReferences, RenderTableReferenceForMeasure, EmitTableReferenceBody);
                _emitter.NewLine();
            }

            if (spec.WhereClause != null)
            {
                _emitter.WriteClauseKeyword("WHERE");
                EmitSearchConditionBody(spec.WhereClause.SearchCondition);
                _emitter.NewLine();
            }
        }
    }

    public override void ExplicitVisit(MergeStatement stmt)
    {
        // 4c D4: MERGE has a partial header scope (MERGE INTO / USING / ON) — maxKw = 10,
        // "MERGE INTO". WHEN clauses emit at statement indent (outside the scope) with action
        // at +IndentSize. OUTPUT after WHENs, in its own one-clause scope (symmetric with
        // BQE's top-level ORDER BY in 4b-iv).
        var spec = stmt.MergeSpecification;

        if (stmt.WithCtesAndXmlNamespaces != null)
        {
            stmt.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        using (_emitter.BeginClauseScope())
        {
            _emitter.WriteClauseKeyword("MERGE INTO");
            EmitTableReferenceBody(spec.Target);
            if (spec.TableAlias != null)
            {
                _generator.GenerateScript(spec.TableAlias, out var aliasText);
                _emitter.Write(" AS " + (aliasText ?? string.Empty).Trim());
            }
            _emitter.NewLine();

            _emitter.WriteClauseKeyword("USING");
            EmitTableReferenceBody(spec.TableReference);
            _emitter.NewLine();

            _emitter.WriteClauseKeyword("ON");
            EmitSearchConditionBody(spec.SearchCondition);
            _emitter.NewLine();
        }

        // WHEN clauses stack; each emits content only (no trailing NL), a separator NL goes
        // between them so the `;` or OUTPUT attaches to the last content line.
        for (int i = 0; i < spec.ActionClauses.Count; i++)
        {
            if (i > 0) _emitter.NewLine();
            EmitMergeActionClause(spec.ActionClauses[i]);
        }

        if (spec.OutputClause != null || spec.OutputIntoClause != null)
        {
            _emitter.NewLine();
            EmitOutputClause(spec);
        }

        // MERGE grammatically requires `;`. Emit on the last content line (either the last
        // action's content or the OUTPUT line). Retires the MERGE-variant harness regressions.
        if (_options.IncludeSemicolons) _emitter.Write(";");
        _emitter.NewLine();
    }

    public override void ExplicitVisit(CreateProcedureStatement stmt) => EmitProcedureBody(stmt, "CREATE PROCEDURE");

    public override void ExplicitVisit(AlterProcedureStatement stmt) => EmitProcedureBody(stmt, "ALTER PROCEDURE");

    public override void ExplicitVisit(CreateOrAlterProcedureStatement stmt) => EmitProcedureBody(stmt, "CREATE OR ALTER PROCEDURE");

    private void EmitProcedureBody(ProcedureStatementBody stmt, string keywordPrefix)
    {
        // Header: CREATE [OR ALTER]/ALTER PROCEDURE <name>
        _emitter.WriteKeyword(keywordPrefix);
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.ProcedureReference, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        // Parameters: comma-list, wrap-at-threshold same as SELECT-list pattern.
        if (stmt.Parameters != null && stmt.Parameters.Count > 0)
        {
            _emitter.NewLine();
            EmitWrappedList(stmt.Parameters, RenderProcedureParameterForMeasure, EmitProcedureParameterBody);
        }

        if (stmt.IsForReplication)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("FOR REPLICATION");
        }

        // WITH ENCRYPTION / RECOMPILE / EXECUTE AS ... — generator render per option, comma-joined.
        if (stmt.Options != null && stmt.Options.Count > 0)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("WITH");
            _emitter.Write(" ");
            for (int i = 0; i < stmt.Options.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.Options[i], out var optText);
                _emitter.Write((optText ?? string.Empty).Trim());
            }
        }

        // AS line; CLR procedures use AS EXTERNAL NAME ... and have no body.
        _emitter.NewLine();
        if (stmt.MethodSpecifier != null)
        {
            _emitter.WriteKeyword("AS");
            _emitter.Write(" ");
            _emitter.WriteKeyword("EXTERNAL NAME");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.MethodSpecifier, out var msText);
            _emitter.Write((msText ?? string.Empty).Trim());
            _emitter.NewLine();
            return;
        }

        _emitter.WriteKeyword("AS");
        _emitter.NewLine();

        // Body: shared with BeginEndBlockStatement and TryCatchHalf — see EmitBodyStatements.
        EmitBodyStatements(stmt.StatementList);
    }

    private string RenderProcedureParameterForMeasure(ProcedureParameter p)
    {
        _generator.GenerateScript(p, out var t);
        return (t ?? string.Empty).Trim();
    }

    private void EmitProcedureParameterBody(ProcedureParameter p)
    {
        _generator.GenerateScript(p, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    public override void ExplicitVisit(BeginEndBlockStatement stmt)
    {
        // BEGIN ATOMIC blocks have Options we don't render — fall back to keep content correct.
        if (stmt is BeginEndAtomicBlockStatement)
        {
            EmitGeneratorRaw(stmt);
            return;
        }

        _emitter.WriteKeyword("BEGIN");
        _emitter.NewLine();
        if (stmt.StatementList != null && stmt.StatementList.Statements.Count > 0)
        {
            using (_emitter.Indent())
            {
                EmitBodyStatements(stmt.StatementList);
            }
            if (!_emitter.AtLineStart) _emitter.NewLine();
        }
        _emitter.WriteKeyword("END");
        _emitter.NewLine();
    }

    public override void ExplicitVisit(TryCatchStatement stmt)
    {
        EmitTryCatchHalf("BEGIN TRY", "END TRY", stmt.TryStatements);
        EmitTryCatchHalf("BEGIN CATCH", "END CATCH", stmt.CatchStatements);
    }

    private void EmitTryCatchHalf(string opener, string closer, StatementList? list)
    {
        _emitter.WriteKeyword(opener);
        _emitter.NewLine();
        if (list != null && list.Statements.Count > 0)
        {
            using (_emitter.Indent())
            {
                EmitBodyStatements(list);
            }
            if (!_emitter.AtLineStart) _emitter.NewLine();
        }
        _emitter.WriteKeyword(closer);
        _emitter.NewLine();
    }

    // 4e-ii-b: shared body recursion for ProcedureBody / BeginEndBlockStatement / TryCatchHalf.
    // Each child dispatches via EmitFragmentDefault — overridden types route to the visitor
    // (SELECT, INSERT, BEGIN/END, IF, WHILE), unmatched fall to generator. After each child
    // we ensure a trailing `;` so control-flow statements (ROLLBACK, BEGIN TRANSACTION, etc.)
    // don't collide with subsequent statements at re-parse — the generator omits `;` for
    // those. Idempotent for already-terminated emissions.
    //
    // Vertical spacing: a blank line separates two adjacent siblings iff at least one of them
    // is "block-level" (multi-line in our output: SELECT/INSERT/UPDATE/DELETE/MERGE/IF/WHILE/
    // BEGIN-END/TRY-CATCH). Consecutive single-liners (DECLARE/SET/RETURN/transaction control/
    // RAISERROR/etc.) stay tight as a group; block statements get breathing room on both
    // sides. Match for what humans naturally write — DECLARE clusters and `IF cond ROLLBACK`
    // pairs hug; an INSERT followed by a SET gets a blank line in between.
    private void EmitBodyStatements(StatementList? list)
    {
        if (list == null || list.Statements.Count == 0) return;
        for (int i = 0; i < list.Statements.Count; i++)
        {
            if (i > 0 && (IsBlockLevelStatement(list.Statements[i - 1]) || IsBlockLevelStatement(list.Statements[i])))
            {
                _emitter.NewLine();
            }
            EmitFragmentDefault(list.Statements[i]);
            _emitter.EnsureTrailingSemicolon();
        }
    }

    private static bool IsBlockLevelStatement(TSqlStatement stmt) => stmt switch
    {
        SelectStatement => true,
        InsertStatement => true,
        UpdateStatement => true,
        DeleteStatement => true,
        MergeStatement => true,
        IfStatement => true,
        WhileStatement => true,
        BeginEndBlockStatement => true,                                    // covers atomic too
        TryCatchStatement => true,
        _ => false,
    };

    public override void ExplicitVisit(IfStatement stmt)
    {
        // 4e-ii: IF on header line; predicate dispatched via EmitConditionalPredicate so
        // subquery-bearing predicates (IF [NOT] EXISTS (...), IF x IN (subq)) break to an
        // indented block via their visitor overrides, rather than rendering inline through
        // the generator's left-aligned-keyword style. Body recursion handles BEGIN/END
        // (lands at IF column) vs single-statement (wrapped in Indent()) by type-check.
        // ELSE branch may itself be an IfStatement (ELSE IF chain) — recurses naturally;
        // see § Known limitations for the indent-stairstep this produces.
        _emitter.WriteKeyword("IF");
        _emitter.Write(" ");
        EmitConditionalPredicate(stmt.Predicate);
        _emitter.NewLine();
        EmitConditionalBody(stmt.ThenStatement);
        if (stmt.ElseStatement != null)
        {
            _emitter.WriteKeyword("ELSE");
            _emitter.NewLine();
            EmitConditionalBody(stmt.ElseStatement);
        }
    }

    public override void ExplicitVisit(WhileStatement stmt)
    {
        // 4e-ii: same shape as IfStatement minus the ELSE branch.
        _emitter.WriteKeyword("WHILE");
        _emitter.Write(" ");
        EmitConditionalPredicate(stmt.Predicate);
        _emitter.NewLine();
        EmitConditionalBody(stmt.Statement);
    }

    private void EmitConditionalBody(TSqlStatement body)
    {
        // BEGIN/END body: don't wrap in Indent() — BEGIN keyword lands at IF/WHILE/ELSE
        // column and the override handles its own +IndentSize for the inner statements.
        // Single-statement body: wrap in Indent() so it lands at +IndentSize. After emit,
        // EnsureTrailingSemicolon (matches 4e-i body sites) so the inner statement carries
        // `;` on re-parse — even when it's a control-flow statement the generator omits.
        if (body is BeginEndBlockStatement)
        {
            EmitFragmentDefault(body);
            _emitter.EnsureTrailingSemicolon();
            return;
        }
        using (_emitter.Indent())
        {
            EmitFragmentDefault(body);
            _emitter.EnsureTrailingSemicolon();
        }
    }

    private void EmitConditionalPredicate(BooleanExpression pred)
    {
        // 4e-ii: separate from EmitSearchConditionBody (which is WHERE/HAVING-specific and
        // assumes an active clause scope for its BBE branch). Same dispatch table minus
        // BBE, plus a BooleanNotExpression branch so `NOT EXISTS (subq)` decomposes into
        // `NOT ` + recurse on the inner ExistsPredicate (which then breaks to block via
        // its own override). AND/OR-mixed predicates fall to the inline scaffold — same
        // limitation as ON / CASE WHEN scopes today.
        switch (pred)
        {
            case ExistsPredicate ep:
                ExplicitVisit(ep);
                return;
            case InPredicate ip when ip.Subquery != null:
                ExplicitVisit(ip);
                return;
            case BooleanComparisonExpression bce when ComparisonHasMultilineSide(bce):
                EmitComparisonOperandScaffold(bce.FirstExpression);
                _emitter.Write(" " + ComparisonOperatorText(bce.ComparisonType) + " ");
                EmitComparisonOperandScaffold(bce.SecondExpression);
                return;
            case BooleanNotExpression bne:
                _emitter.WriteKeyword("NOT");
                _emitter.Write(" ");
                EmitConditionalPredicate(bne.Expression);
                return;
            default:
                EmitInlineBooleanScaffold(pred);
                return;
        }
    }

    private void EmitMergeActionClause(MergeActionClause clause)
    {
        // WHEN [NOT] MATCHED [BY TARGET|SOURCE] [AND <cond>] THEN\n<action at +IndentSize>
        // No trailing NewLine — the MERGE loop emits a separator NL between clauses so `;`
        // attaches to the last content line.
        _emitter.WriteKeyword(MergeConditionKeyword(clause.Condition));
        if (clause.SearchCondition != null)
        {
            _emitter.Write(" ");
            _emitter.WriteKeyword("AND");
            _emitter.Write(" ");
            _generator.GenerateScript(clause.SearchCondition, out var condText);
            _emitter.Write((condText ?? string.Empty).Trim());
        }
        _emitter.Write(" ");
        _emitter.WriteKeyword("THEN");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            EmitMergeAction(clause.Action);
        }
    }

    private static string MergeConditionKeyword(MergeCondition c) => c switch
    {
        MergeCondition.Matched => "WHEN MATCHED",
        MergeCondition.NotMatchedByTarget => "WHEN NOT MATCHED BY TARGET",
        MergeCondition.NotMatchedBySource => "WHEN NOT MATCHED BY SOURCE",
        _ => "WHEN MATCHED",
    };

    private void EmitMergeAction(MergeAction action)
    {
        if (action is UpdateMergeAction uma)
        {
            _emitter.WriteKeyword("UPDATE SET ");
            EmitWrappedList(uma.SetClauses, RenderSetClauseForMeasure, EmitSetClauseBody);
            return;
        }
        if (action is DeleteMergeAction) { _emitter.WriteKeyword("DELETE"); return; }
        if (action is InsertMergeAction ima)
        {
            _emitter.WriteKeyword("INSERT ");
            if (ima.Columns != null && ima.Columns.Count > 0)
            {
                _emitter.Write("(");
                for (int i = 0; i < ima.Columns.Count; i++)
                {
                    if (i > 0) _emitter.Write(", ");
                    _generator.GenerateScript(ima.Columns[i], out var ct);
                    _emitter.Write((ct ?? string.Empty).Trim());
                }
                _emitter.Write(")");
                _emitter.NewLine();
            }
            EmitInsertSource(ima.Source);
            return;
        }
        EmitGeneratorRaw(action);
    }

    private void EmitInsertSource(InsertSource source)
    {
        if (source is ValuesInsertSource vis) { EmitValuesInsertSource(vis); return; }
        if (source is SelectInsertSource sis)
        {
            // Select property is a QueryExpression (QuerySpecification | BinaryQueryExpression).
            EmitSubqueryQueryExpression(sis.Select);
            return;
        }
        if (source is ExecuteInsertSource eis)
        {
            _generator.GenerateScript(eis.Execute, out var t);
            _emitter.Write((t ?? string.Empty).Trim());
            return;
        }
        EmitGeneratorRaw(source);
    }

    private void EmitValuesInsertSource(ValuesInsertSource vis)
    {
        if (vis.IsDefaultValues) { _emitter.WriteKeyword("DEFAULT VALUES"); return; }

        _emitter.WriteKeyword("VALUES");
        var rows = vis.RowValues;
        if (rows.Count == 1)
        {
            // Single row: inline after VALUES when short enough.
            _generator.GenerateScript(rows[0], out var rt);
            var row = (rt ?? string.Empty).Trim();
            if (row.Length <= _options.MaxLineLength * 2 / 3)
            {
                _emitter.Write(" " + row);
            }
            else
            {
                _emitter.NewLine();
                using (_emitter.Indent()) { _emitter.Write(row); }
            }
            return;
        }

        // D5: multi-row VALUES always wraps — rows stack one per line at +IndentSize,
        // trailing commas per D3.
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) { _emitter.Write(","); _emitter.NewLine(); }
                _generator.GenerateScript(rows[i], out var rt);
                _emitter.Write((rt ?? string.Empty).Trim());
            }
        }
    }

    private void EmitOutputClause(DataModificationSpecification spec)
    {
        // 4c D5: shared across INSERT / UPDATE / DELETE / MERGE. ScriptDom populates exactly
        // one of OutputClause (just columns) or OutputIntoClause (columns + INTO target).
        // In-scope (UPDATE / DELETE): OUTPUT / INTO as right-aligned clause keywords.
        // Out-of-scope (INSERT / MERGE): OUTPUT at current indent, INTO on next line at +IndentSize.
        var outputInto = spec.OutputIntoClause;
        var outputOnly = spec.OutputClause;
        if (outputInto == null && outputOnly == null) return;
        var cols = outputInto?.SelectColumns ?? outputOnly.SelectColumns;

        if (_emitter.InClauseScope)
        {
            _emitter.WriteClauseKeyword("OUTPUT");
            EmitWrappedList(cols, RenderSelectElementForMeasure, EmitSelectElementBody);
            _emitter.NewLine();
            if (outputInto != null)
            {
                _emitter.WriteClauseKeyword("INTO");
                EmitOutputIntoBody(outputInto);
                _emitter.NewLine();
            }
        }
        else
        {
            // Skip the leading NewLine when already at line start (MERGE's last action clause
            // leaves the cursor fresh-line after emitting its trailing NewLine).
            if (!_emitter.AtLineStart) _emitter.NewLine();
            _emitter.WriteKeyword("OUTPUT");
            _emitter.Write(" ");
            for (int i = 0; i < cols.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                EmitSelectElementBody(cols[i]);
            }
            if (outputInto != null)
            {
                _emitter.NewLine();
                using (_emitter.Indent())
                {
                    _emitter.WriteKeyword("INTO");
                    _emitter.Write(" ");
                    EmitOutputIntoBody(outputInto);
                }
            }
        }
    }

    private void EmitOutputIntoBody(OutputIntoClause into)
    {
        EmitTableReferenceBody(into.IntoTable);
        if (into.IntoTableColumns != null && into.IntoTableColumns.Count > 0)
        {
            _emitter.Write(" (");
            for (int i = 0; i < into.IntoTableColumns.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(into.IntoTableColumns[i], out var ct);
                _emitter.Write((ct ?? string.Empty).Trim());
            }
            _emitter.Write(")");
        }
    }

    private string RenderSetClauseForMeasure(SetClause sc)
    {
        _generator.GenerateScript(sc, out var t);
        return (t ?? string.Empty).Trim();
    }

    private void EmitSetClauseBody(SetClause sc)
    {
        // Generator scaffold per-assignment. Subquery-in-NewValue surfaces as a multi-line
        // generator output; EmitWrappedList's first-line measurement already accounts for that.
        // Breaking the subquery-in-NewValue to block is 4f's job (arbitrary ScalarExpression).
        _generator.GenerateScript(sc, out var t);
        _emitter.Write((t ?? string.Empty).Trim());
    }

    // Temporary 4a scaffold — removed after 4g when all fragment types have visitor overrides.
    // Every 4b–4f override replaces one fragment type's fallback here with custom emission.
    // If this method (and the Sql170ScriptGenerator instance above) still exists after 4g, that's a bug.
    internal void EmitFragmentDefault(TSqlFragment fragment)
    {
        // Each slice adds another routing branch here; 4g deletes the whole method.
        if (fragment is SelectStatement selectStatement) { ExplicitVisit(selectStatement); return; }
        if (fragment is ScalarSubquery sub) { ExplicitVisit(sub); return; }
        if (fragment is InPredicate ip && ip.Subquery != null) { ExplicitVisit(ip); return; }
        if (fragment is ExistsPredicate ep) { ExplicitVisit(ep); return; }
        if (fragment is QualifiedJoin qj) { ExplicitVisit(qj); return; }
        if (fragment is UnqualifiedJoin uj) { ExplicitVisit(uj); return; }
        if (fragment is QueryDerivedTable qdt) { ExplicitVisit(qdt); return; }
        if (fragment is WithCtesAndXmlNamespaces wcn) { ExplicitVisit(wcn); return; }
        if (fragment is CommonTableExpression cte) { ExplicitVisit(cte); return; }
        if (fragment is SearchedCaseExpression sce) { ExplicitVisit(sce); return; }
        if (fragment is SimpleCaseExpression sice) { ExplicitVisit(sice); return; }
        if (fragment is BinaryQueryExpression bqe) { ExplicitVisit(bqe); return; }
        if (fragment is BooleanBinaryExpression bbe) { ExplicitVisit(bbe); return; }
        if (fragment is InsertStatement ins) { ExplicitVisit(ins); return; }
        if (fragment is UpdateStatement upd) { ExplicitVisit(upd); return; }
        if (fragment is DeleteStatement del) { ExplicitVisit(del); return; }
        if (fragment is MergeStatement mrg) { ExplicitVisit(mrg); return; }
        if (fragment is CreateProcedureStatement cp) { ExplicitVisit(cp); return; }
        if (fragment is AlterProcedureStatement ap) { ExplicitVisit(ap); return; }
        if (fragment is CreateOrAlterProcedureStatement coap) { ExplicitVisit(coap); return; }
        if (fragment is BeginEndBlockStatement beb) { ExplicitVisit(beb); return; }
        if (fragment is TryCatchStatement tc) { ExplicitVisit(tc); return; }
        if (fragment is IfStatement ifs) { ExplicitVisit(ifs); return; }
        if (fragment is WhileStatement ws) { ExplicitVisit(ws); return; }

        EmitGeneratorRaw(fragment);
    }

    // 4b-iii-b: comma-joined list with wrap-at-threshold. Measures total via renderForMeasure
    // (cheap generator output per element — not through the visitor). For multi-line element
    // renderings (e.g. subqueries in SELECT), only the first line contributes to the
    // measurement — the element handles its own break-to-block internally, so the *list* only
    // needs to wrap when the inline prefix would push the line over width. Emits each element
    // inline (", " separator) when total fits under MaxLineLength * 2/3, else NewLine-separates
    // with trailing commas (CommaStyle = Trailing per D3). Continuation lines land at the body
    // column automatically via the active clause scope's flush-time indent.
    private void EmitWrappedList<T>(IList<T> items, System.Func<T, string> renderForMeasure, System.Action<T> emit)
    {
        if (items == null || items.Count == 0) return;
        if (items.Count == 1) { emit(items[0]); return; }

        int totalLen = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var text = renderForMeasure(items[i]) ?? string.Empty;
            var nl = text.IndexOf('\n');
            totalLen += nl >= 0 ? nl : text.Length;                        // first-line length only
        }
        totalLen += (items.Count - 1) * 2;                                 // ", " separators
        bool wrap = totalLen > _options.MaxLineLength * 2 / 3;

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                if (wrap) { _emitter.Write(","); _emitter.NewLine(); }
                else _emitter.Write(", ");
            }
            emit(items[i]);
        }
    }

    // Raw generator emission — bypasses the routing in EmitFragmentDefault. Use this when an
    // override needs to fall back to generator for its *own* fragment type (calling
    // EmitFragmentDefault there would re-enter the override and infinite-loop).
    private void EmitGeneratorRaw(TSqlFragment fragment)
    {
        _generator.GenerateScript(fragment, out var text);
        if (string.IsNullOrEmpty(text)) return;
        foreach (var line in text.TrimEnd('\r', '\n').Split('\n'))
        {
            _emitter.WriteLine(line.TrimEnd('\r'));
        }
    }
}
