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
        // 4d-ii: peel QueryParenthesisExpression wrappers (e.g. `AS (SELECT ...)` view bodies)
        // so the QuerySpec/BQE dispatcher operates on the underlying expression. Guard: only
        // unwrap if the parens carry no clauses of their own (OrderBy / Offset / For at the
        // parens level would be silently dropped otherwise — fall through to generator in that
        // case via EmitFragmentDefault below).
        var qe = statement.QueryExpression;
        while (qe is QueryParenthesisExpression qpe
               && qpe.OrderByClause == null
               && qpe.OffsetClause == null
               && qpe.ForClause == null)
        {
            qe = qpe.QueryExpression;
        }

        // Niche SelectStatement features not modelled in the visitor (`Into` for SELECT INTO,
        // `On` for legacy SELECT…ON syntax, `ComputeClauses`, `OptimizerHints`) — bail to the
        // generator on the whole statement BEFORE any visitor emission, otherwise we'd
        // double-emit (run the QuerySpec override, then emit a generator-rendered copy on top).
        // Use EmitGeneratorRaw to bypass dispatch — EmitFragmentDefault would re-enter this
        // override and stack-overflow (the original 4e-iii smoke crash on Sorgu/2161.sql's
        // `SELECT * INTO #weight FROM (...)`).
        if (statement.Into != null || statement.On != null
            || (statement.ComputeClauses != null && statement.ComputeClauses.Count > 0)
            || (statement.OptimizerHints != null && statement.OptimizerHints.Count > 0))
        {
            EmitGeneratorRaw(statement);
            return;
        }

        if (statement.WithCtesAndXmlNamespaces != null)
        {
            statement.WithCtesAndXmlNamespaces.Accept(this);
            _emitter.NewLine();
        }

        if (qe is QuerySpecification qs)
        {
            qs.Accept(this);
        }
        else if (qe != null)
        {
            EmitFragmentDefault(qe);
        }
    }

    public override void ExplicitVisit(QuerySpecification q)
    {
        using (_emitter.BeginClauseScope())
        {
            // SELECT [ALL|DISTINCT] [TOP n [PERCENT] [WITH TIES]] <select-list>
            _emitter.WriteClauseKeyword("SELECT");
            if (q.UniqueRowFilter == UniqueRowFilter.Distinct) _emitter.Write("DISTINCT ");
            else if (q.UniqueRowFilter == UniqueRowFilter.All) _emitter.Write("ALL ");
            if (q.TopRowFilter != null) EmitTopRowFilter(q.TopRowFilter);
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

            if (q.OffsetClause != null)
            {
                _emitter.WriteClauseKeyword("OFFSET");
                EmitOffsetClauseBody(q.OffsetClause);
                _emitter.NewLine();
            }

            if (q.ForClause != null)
            {
                _emitter.WriteClauseKeyword("FOR");
                EmitForClauseBody(q.ForClause);
                _emitter.NewLine();
            }
        }
    }

    // 4f-ii: TOP renders inline within the SELECT clause body — `TOP <expr> [PERCENT] [WITH TIES] `.
    private void EmitTopRowFilter(TopRowFilter top)
    {
        _emitter.Write("TOP ");
        _generator.GenerateScript(top.Expression, out var exprText);
        _emitter.Write((exprText ?? string.Empty).Trim());
        if (top.Percent) _emitter.Write(" PERCENT");
        if (top.WithTies) _emitter.Write(" WITH TIES");
        _emitter.Write(" ");
    }

    // 4f-ii: OFFSET as its own clause keyword, body holds OFFSET <expr> ROWS plus optional
    // FETCH NEXT <expr> ROWS ONLY inline. Splitting earns nothing for the corpus.
    private void EmitOffsetClauseBody(OffsetClause off)
    {
        _generator.GenerateScript(off.OffsetExpression, out var offText);
        _emitter.Write((offText ?? string.Empty).Trim() + " ROWS");
        if (off.FetchExpression != null)
        {
            _generator.GenerateScript(off.FetchExpression, out var fetchText);
            _emitter.Write(" FETCH NEXT " + (fetchText ?? string.Empty).Trim() + " ROWS ONLY");
        }
    }

    // 4f-ii: FOR body via prefix-strip — `FOR XML PATH ('')` → strip "FOR " → `XML PATH ('')`.
    // XmlForClause / JsonForClause options have non-trivial textual rendering (AUTO / PATH /
    // ELEMENTS / ROOT('r') / BINARY BASE64 / INCLUDE_NULL_VALUES); the generator gets them right.
    private void EmitForClauseBody(ForClause fc)
    {
        _generator.GenerateScript(fc, out var fcText);
        var body = (fcText ?? string.Empty).Trim();
        if (body.StartsWith("FOR ", StringComparison.OrdinalIgnoreCase)) body = body.Substring(4);
        _emitter.Write(body);
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
        if (tableRef is PivotedTableReference pvt) { ExplicitVisit(pvt); return; }
        if (tableRef is UnpivotedTableReference upvt) { ExplicitVisit(upvt); return; }
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

    // 4f: <source> PIVOT (agg(val) FOR col IN (v1, v2, ...)) AS alias.
    // Source recurses through EmitTableReferenceBody so QueryDerivedTable / nested PIVOT etc.
    // dispatch correctly. PIVOT clause renders inline when its assembled length fits under
    // MaxLineLength; otherwise the IN-list breaks one value per line at +IndentSize. ForPath
    // (graph SQL) trips a defensive generator fallback — out of scope this slice.
    public override void ExplicitVisit(PivotedTableReference pvt)
    {
        if (pvt.ForPath) { EmitGeneratorRaw(pvt); return; }

        EmitTableReferenceBody(pvt.TableReference);

        var agg = JoinPart(pvt.AggregateFunctionIdentifier);
        var valueArgs = JoinScalars(pvt.ValueColumns);
        var pivotCol = JoinPart(pvt.PivotColumn);
        var inValues = RenderEach(pvt.InColumns);
        var alias = pvt.Alias != null ? JoinPart(pvt.Alias) : string.Empty;

        var header = "PIVOT (" + agg + "(" + valueArgs + ") FOR " + pivotCol + " IN (";
        var inlineInList = string.Join(", ", inValues);
        var tail = ")) AS " + alias;
        var inline = header + inlineInList + tail;

        if (inline.Length <= _options.MaxLineLength)
        {
            _emitter.Write(" " + inline);
            return;
        }

        _emitter.Write(" " + header);
        _emitter.NewLine();
        var indent = new string(' ', _options.IndentSize);
        for (int i = 0; i < inValues.Count; i++)
        {
            _emitter.Write(indent + inValues[i] + (i < inValues.Count - 1 ? "," : string.Empty));
            _emitter.NewLine();
        }
        _emitter.Write(tail);
    }

    // 4f: <source> UNPIVOT (val FOR col IN (c1, c2, ...)) AS alias. ValueColumn is a singular
    // Identifier (not a list); InColumns are ColumnReferenceExpression (not Identifier as in
    // PIVOT). Same wrap rule as PIVOT.
    public override void ExplicitVisit(UnpivotedTableReference upvt)
    {
        if (upvt.ForPath) { EmitGeneratorRaw(upvt); return; }

        EmitTableReferenceBody(upvt.TableReference);

        var valueCol = JoinPart(upvt.ValueColumn);
        var pivotCol = JoinPart(upvt.PivotColumn);
        var inValues = RenderEach(upvt.InColumns);
        var alias = upvt.Alias != null ? JoinPart(upvt.Alias) : string.Empty;

        var header = "UNPIVOT (" + valueCol + " FOR " + pivotCol + " IN (";
        var inlineInList = string.Join(", ", inValues);
        var tail = ")) AS " + alias;
        var inline = header + inlineInList + tail;

        if (inline.Length <= _options.MaxLineLength)
        {
            _emitter.Write(" " + inline);
            return;
        }

        _emitter.Write(" " + header);
        _emitter.NewLine();
        var indent = new string(' ', _options.IndentSize);
        for (int i = 0; i < inValues.Count; i++)
        {
            _emitter.Write(indent + inValues[i] + (i < inValues.Count - 1 ? "," : string.Empty));
            _emitter.NewLine();
        }
        _emitter.Write(tail);
    }

    private string JoinPart(TSqlFragment frag)
    {
        _generator.GenerateScript(frag, out var t);
        return (t ?? string.Empty).Trim();
    }

    private System.Collections.Generic.List<string> RenderEach<T>(System.Collections.Generic.IList<T> items) where T : TSqlFragment
    {
        var list = new System.Collections.Generic.List<string>(items.Count);
        for (int i = 0; i < items.Count; i++) list.Add(JoinPart(items[i]));
        return list;
    }

    private string JoinScalars<T>(System.Collections.Generic.IList<T> items) where T : TSqlFragment
        => string.Join(", ", RenderEach(items));

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

    public override void ExplicitVisit(CreateViewStatement stmt) => EmitViewBody(stmt, "CREATE VIEW");

    public override void ExplicitVisit(AlterViewStatement stmt) => EmitViewBody(stmt, "ALTER VIEW");

    public override void ExplicitVisit(CreateOrAlterViewStatement stmt) => EmitViewBody(stmt, "CREATE OR ALTER VIEW");

    private void EmitViewBody(ViewStatementBody stmt, string keywordPrefix)
    {
        // Materialized views (Synapse) carry extra DDL we don't model — fall back.
        if (stmt.IsMaterialized) { EmitGeneratorRaw(stmt); return; }

        _emitter.WriteKeyword(keywordPrefix);
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.SchemaObjectName, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        // Optional column list (a, b). Inline-only — mirrors CommonTableExpression columns
        // (cf. ExplicitVisit(CommonTableExpression) above). Promotion to EmitWrappedList is
        // deferred jointly with CTE columns until corpus surfaces a long view column list.
        if (stmt.Columns != null && stmt.Columns.Count > 0)
        {
            _emitter.Write(" (");
            for (int i = 0; i < stmt.Columns.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.Columns[i], out var colText);
                _emitter.Write((colText ?? string.Empty).Trim());
            }
            _emitter.Write(")");
        }

        // WITH ENCRYPTION / SCHEMABINDING / VIEW_METADATA — generator-render per option,
        // comma-joined. Same pattern as procedure WITH options.
        if (stmt.ViewOptions != null && stmt.ViewOptions.Count > 0)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("WITH");
            _emitter.Write(" ");
            for (int i = 0; i < stmt.ViewOptions.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.ViewOptions[i], out var optText);
                _emitter.Write((optText ?? string.Empty).Trim());
            }
        }

        _emitter.NewLine();
        _emitter.WriteKeyword("AS");
        _emitter.NewLine();

        // Body SELECT routes through EmitFragmentDefault → SelectStatement override (handles
        // CTE prelude, QuerySpec dispatch, niche fallback). Body is at indent 0 — flat, no
        // extra Indent() — matches SSMS / GittyExport convention for view bodies.
        EmitFragmentDefault(stmt.SelectStatement);

        // WITH CHECK OPTION trailer — grammar puts it before `;`. SelectStatement leaves a
        // trailing `;` and NewLine; emit on a fresh line at col 0.
        if (stmt.WithCheckOption)
        {
            if (!_emitter.AtLineStart) _emitter.NewLine();
            _emitter.WriteKeyword("WITH CHECK OPTION");
            _emitter.NewLine();
        }
    }

    public override void ExplicitVisit(CreateFunctionStatement stmt) => EmitFunctionBody(stmt, "CREATE FUNCTION");

    public override void ExplicitVisit(AlterFunctionStatement stmt) => EmitFunctionBody(stmt, "ALTER FUNCTION");

    public override void ExplicitVisit(CreateOrAlterFunctionStatement stmt) => EmitFunctionBody(stmt, "CREATE OR ALTER FUNCTION");

    private void EmitFunctionBody(FunctionStatementBody stmt, string keywordPrefix)
    {
        // Header: CREATE [OR ALTER] / ALTER FUNCTION <name>
        _emitter.WriteKeyword(keywordPrefix);
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.Name, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        // Parameters in parens. Functions REQUIRE parens around the list even when empty
        // (unlike procs, which allow `CREATE PROCEDURE dbo.usp AS ...` — the parser rejects
        // `CREATE FUNCTION dbo.fn RETURNS ...` outright). Empty list → `()` inline; non-empty
        // → multi-line with paren on its own line, params indented, matching the Sorgu corpus
        // and SSMS canonical layout. ProcedureParameter is the shared type from
        // ProcedureStatementBodyBase, so EmitProcedureParameterBody is reused as-is.
        if (stmt.Parameters == null || stmt.Parameters.Count == 0)
        {
            _emitter.Write("()");
        }
        else
        {
            _emitter.NewLine();
            _emitter.Write("(");
            _emitter.NewLine();
            using (_emitter.Indent())
            {
                for (int i = 0; i < stmt.Parameters.Count; i++)
                {
                    if (i > 0)
                    {
                        _emitter.Write(",");
                        _emitter.NewLine();
                    }
                    EmitProcedureParameterBody(stmt.Parameters[i]);
                }
            }
            _emitter.NewLine();
            _emitter.Write(")");
        }

        // RETURNS clause — three concrete ReturnType subclasses:
        //   ScalarFunctionReturnType { DataType }       — RETURNS <type>
        //   SelectFunctionReturnType { SelectStatement } — INLINE TVF (RETURNS TABLE)
        //   TableValuedFunctionReturnType { DeclareTableVariableBody } — MULTI-STMT TVF
        // (counterintuitive ScriptDom naming: SelectFunctionReturnType is INLINE; TableValued is
        // multi-stmt). Multi-stmt TVF column DDL lands here as a generator-rendered single line —
        // per-column wrap is 4d-v territory.
        switch (stmt.ReturnType)
        {
            case ScalarFunctionReturnType srt:
                _emitter.NewLine();
                _emitter.WriteKeyword("RETURNS");
                _emitter.Write(" ");
                _generator.GenerateScript(srt.DataType, out var dtText);
                _emitter.Write((dtText ?? string.Empty).Trim());
                break;
            case SelectFunctionReturnType _:
                _emitter.NewLine();
                _emitter.WriteKeyword("RETURNS TABLE");
                break;
            case TableValuedFunctionReturnType trt:
                // 4d-v: was generator-rendered as a single line (per-column squashed); now
                // dispatches the variable-name + TABLE header here, then reuses
                // EmitTableDefinitionBody for the column / constraint block — same shape as
                // CREATE TABLE so a multi-stmt TVF with a long column list reads identically.
                _emitter.NewLine();
                _emitter.WriteKeyword("RETURNS");
                _emitter.Write(" ");
                _generator.GenerateScript(trt.DeclareTableVariableBody.VariableName, out var vnText);
                _emitter.Write((vnText ?? string.Empty).Trim());
                _emitter.Write(" ");
                _emitter.WriteKeyword("TABLE");
                if (trt.DeclareTableVariableBody.Definition != null)
                {
                    EmitTableDefinitionBody(trt.DeclareTableVariableBody.Definition);
                }
                break;
            default:
                EmitGeneratorRaw(stmt);
                return;
        }

        // ORDER hint (inline TVF) — rare. Generator-render if present.
        if (stmt.OrderHint != null)
        {
            _emitter.NewLine();
            _generator.GenerateScript(stmt.OrderHint, out var ohText);
            _emitter.Write((ohText ?? string.Empty).Trim());
        }

        // WITH SCHEMABINDING / RETURNS NULL ON NULL INPUT / CALLED ON NULL INPUT / EXECUTE AS —
        // generator-render per option, comma-joined. Same pattern as procs / views.
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

        // AS line; CLR functions use AS EXTERNAL NAME ... and have no body.
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

        // Body branches by shape:
        //   scalar / multi-stmt TVF: StatementList[0] is always BeginEndBlockStatement
        //     (BEGIN/END is captured as a real fragment, not implicit). Routing through
        //     EmitBodyStatements + the existing BeginEndBlockStatement override emits the
        //     wrapping naturally.
        //   inline TVF: StatementList is null; SELECT lives at ReturnType.SelectStatement.
        //     Mirror ScalarSubquery's break-to-block pattern: `RETURN (` + NL + Indent + recurse
        //     + AtLineStart guard + `)`.
        if (stmt.ReturnType is SelectFunctionReturnType inline)
        {
            _emitter.WriteKeyword("RETURN");
            _emitter.Write(" (");
            _emitter.NewLine();
            using (_emitter.Indent())
            {
                EmitFragmentDefault(inline.SelectStatement);
            }
            if (!_emitter.AtLineStart) _emitter.NewLine();
            _emitter.Write(")");
            _emitter.NewLine();
            return;
        }

        EmitBodyStatements(stmt.StatementList);
    }

    public override void ExplicitVisit(CreateTriggerStatement stmt) => EmitTriggerBody(stmt, "CREATE TRIGGER");

    public override void ExplicitVisit(AlterTriggerStatement stmt) => EmitTriggerBody(stmt, "ALTER TRIGGER");

    public override void ExplicitVisit(CreateOrAlterTriggerStatement stmt) => EmitTriggerBody(stmt, "CREATE OR ALTER TRIGGER");

    private void EmitTriggerBody(TriggerStatementBody stmt, string keywordPrefix)
    {
        // Header: CREATE [OR ALTER] / ALTER TRIGGER <name>
        _emitter.WriteKeyword(keywordPrefix);
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.Name, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        // ON <target> — TriggerObject renders the literal for Normal (dbo.t),
        // Database (DATABASE), and AllServer (ALL SERVER) scopes uniformly via the generator.
        _emitter.NewLine();
        _emitter.WriteKeyword("ON");
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.TriggerObject, out var targetText);
        _emitter.Write((targetText ?? string.Empty).Trim());

        // WITH <opts> — TriggerOption (ENCRYPTION) and ExecuteAsTriggerOption rendered
        // via generator and comma-joined. Same pattern as procs / views / funcs.
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

        // Timing + event list. TriggerType: After / InsteadOf / For. TriggerActions are
        // rendered via the generator — handles INSERT/UPDATE/DELETE (DML), Event with
        // EventTypeContainer/EventGroupContainer (DDL), and LogOn uniformly.
        _emitter.NewLine();
        _emitter.WriteKeyword(TriggerTypeKeyword(stmt.TriggerType));
        _emitter.Write(" ");
        for (int i = 0; i < stmt.TriggerActions.Count; i++)
        {
            if (i > 0) _emitter.Write(", ");
            _generator.GenerateScript(stmt.TriggerActions[i], out var actText);
            _emitter.Write((actText ?? string.Empty).Trim());
        }

        if (stmt.IsNotForReplication)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("NOT FOR REPLICATION");
        }

        _emitter.NewLine();
        _emitter.WriteKeyword("AS");
        _emitter.NewLine();

        // Body recurses through the visitor — when StatementList[0] is a captured
        // BeginEndBlockStatement (the common case), the override emits BEGIN/END.
        // Otherwise emits the single statement flat. Mirrors procs (not functions,
        // where ScriptDom always wraps in BEGIN/END at parse time).
        EmitBodyStatements(stmt.StatementList);
    }

    private static string TriggerTypeKeyword(TriggerType t) => t switch
    {
        TriggerType.After => "AFTER",
        TriggerType.InsteadOf => "INSTEAD OF",
        TriggerType.For => "FOR",
        _ => "FOR",
    };

    public override void ExplicitVisit(CreateTableStatement stmt) => EmitCreateTableBody(stmt);

    public override void ExplicitVisit(AlterTableAddTableElementStatement stmt)
    {
        // ALTER TABLE <name> [WITH CHECK|NOCHECK] ADD <column-or-constraint-list>
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        EmitExistingRowsCheck(stmt.ExistingRowsCheckEnforcement);
        _emitter.WriteKeyword("ADD");
        // Single column / constraint: render inline on the same line. Multiple: render via
        // EmitTableDefinition for the parenthesised, indented block — the same shape used for
        // CREATE TABLE. ScriptDom allows multiple ADDs per statement, comma-separated.
        var colCount = stmt.Definition?.ColumnDefinitions?.Count ?? 0;
        var conCount = stmt.Definition?.TableConstraints?.Count ?? 0;
        var idxCount = stmt.Definition?.Indexes?.Count ?? 0;
        var totalElems = colCount + conCount + idxCount;
        if (totalElems == 1)
        {
            _emitter.Write(" ");
            if (colCount == 1) EmitColumnDefinition(stmt.Definition!.ColumnDefinitions[0]);
            else if (conCount == 1) EmitConstraintDefinition(stmt.Definition!.TableConstraints[0]);
            else EmitGeneratorRaw(stmt.Definition!.Indexes[0]);
            _emitter.NewLine();
        }
        else
        {
            EmitTableDefinitionBody(stmt.Definition!);
        }
    }

    public override void ExplicitVisit(AlterTableDropTableElementStatement stmt)
    {
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        _emitter.WriteKeyword("DROP");
        _emitter.Write(" ");
        for (int i = 0; i < stmt.AlterTableDropTableElements.Count; i++)
        {
            if (i > 0) _emitter.Write(", ");
            var elem = stmt.AlterTableDropTableElements[i];
            // First element: the keyword (COLUMN / CONSTRAINT). Subsequent elements omit the
            // keyword if the same kind, as DROP COLUMN a, b is the source-canonical form.
            if (i == 0)
            {
                _emitter.WriteKeyword(elem.TableElementType == TableElementType.Column ? "COLUMN" : "CONSTRAINT");
                _emitter.Write(" ");
            }
            if (elem.IsIfExists) { _emitter.WriteKeyword("IF EXISTS"); _emitter.Write(" "); }
            _generator.GenerateScript(elem.Name, out var nameText);
            _emitter.Write((nameText ?? string.Empty).Trim());
        }
        _emitter.NewLine();
    }

    public override void ExplicitVisit(AlterTableAlterColumnStatement stmt)
    {
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        _emitter.WriteKeyword("ALTER COLUMN");
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.ColumnIdentifier, out var idText);
        _emitter.Write((idText ?? string.Empty).Trim());
        if (stmt.DataType != null)
        {
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.DataType, out var dtText);
            _emitter.Write((dtText ?? string.Empty).Trim());
        }
        switch (stmt.AlterTableAlterColumnOption)
        {
            case AlterTableAlterColumnOption.Null: _emitter.Write(" "); _emitter.WriteKeyword("NULL"); break;
            case AlterTableAlterColumnOption.NotNull: _emitter.Write(" "); _emitter.WriteKeyword("NOT NULL"); break;
            case AlterTableAlterColumnOption.AddRowGuidCol: _emitter.Write(" "); _emitter.WriteKeyword("ADD ROWGUIDCOL"); break;
            case AlterTableAlterColumnOption.DropRowGuidCol: _emitter.Write(" "); _emitter.WriteKeyword("DROP ROWGUIDCOL"); break;
            case AlterTableAlterColumnOption.AddPersisted: _emitter.Write(" "); _emitter.WriteKeyword("ADD PERSISTED"); break;
            case AlterTableAlterColumnOption.DropPersisted: _emitter.Write(" "); _emitter.WriteKeyword("DROP PERSISTED"); break;
        }
        if (stmt.Collation != null)
        {
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.Collation, out var colText);
            _emitter.Write((colText ?? string.Empty).Trim());
        }
        _emitter.NewLine();
    }

    public override void ExplicitVisit(AlterTableSwitchStatement stmt)
    {
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        _emitter.WriteKeyword("SWITCH");
        if (stmt.SourcePartitionNumber != null)
        {
            _emitter.Write(" ");
            _emitter.WriteKeyword("PARTITION");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.SourcePartitionNumber, out var srcText);
            _emitter.Write((srcText ?? string.Empty).Trim());
        }
        _emitter.Write(" ");
        _emitter.WriteKeyword("TO");
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.TargetTable, out var tgtText);
        _emitter.Write((tgtText ?? string.Empty).Trim());
        if (stmt.TargetPartitionNumber != null)
        {
            _emitter.Write(" ");
            _emitter.WriteKeyword("PARTITION");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.TargetPartitionNumber, out var tpText);
            _emitter.Write((tpText ?? string.Empty).Trim());
        }
        _emitter.NewLine();
    }

    public override void ExplicitVisit(AlterTableTriggerModificationStatement stmt)
    {
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        _emitter.WriteKeyword(stmt.TriggerEnforcement == TriggerEnforcement.Enable ? "ENABLE TRIGGER" : "DISABLE TRIGGER");
        _emitter.Write(" ");
        if (stmt.All)
        {
            _emitter.WriteKeyword("ALL");
        }
        else
        {
            for (int i = 0; i < stmt.TriggerNames.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.TriggerNames[i], out var nText);
                _emitter.Write((nText ?? string.Empty).Trim());
            }
        }
        _emitter.NewLine();
    }

    public override void ExplicitVisit(AlterTableConstraintModificationStatement stmt)
    {
        EmitAlterTableHeader(stmt.SchemaObjectName);
        _emitter.NewLine();
        EmitExistingRowsCheck(stmt.ExistingRowsCheckEnforcement);
        _emitter.WriteKeyword(stmt.ConstraintEnforcement == ConstraintEnforcement.NoCheck ? "NOCHECK CONSTRAINT" : "CHECK CONSTRAINT");
        _emitter.Write(" ");
        if (stmt.All)
        {
            _emitter.WriteKeyword("ALL");
        }
        else
        {
            for (int i = 0; i < stmt.ConstraintNames.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.ConstraintNames[i], out var nText);
                _emitter.Write((nText ?? string.Empty).Trim());
            }
        }
        _emitter.NewLine();
    }

    private void EmitAlterTableHeader(SchemaObjectName name)
    {
        _emitter.WriteKeyword("ALTER TABLE");
        _emitter.Write(" ");
        _generator.GenerateScript(name, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());
    }

    private void EmitExistingRowsCheck(ConstraintEnforcement e)
    {
        // WITH CHECK / WITH NOCHECK on the same line as the ADD/CHECK CONSTRAINT keyword.
        if (e == ConstraintEnforcement.Check) { _emitter.WriteKeyword("WITH CHECK"); _emitter.Write(" "); }
        else if (e == ConstraintEnforcement.NoCheck) { _emitter.WriteKeyword("WITH NOCHECK"); _emitter.Write(" "); }
    }

    private void EmitCreateTableBody(CreateTableStatement stmt)
    {
        // Header: CREATE TABLE <name>
        _emitter.WriteKeyword("CREATE TABLE");
        _emitter.Write(" ");
        _generator.GenerateScript(stmt.SchemaObjectName, out var nameText);
        _emitter.Write((nameText ?? string.Empty).Trim());

        // Body: ( cols, table-constraints, indexes ) with no blank-line separator between
        // groups (D2 — matches SSMS canonical and the Sorgu corpus).
        if (stmt.Definition != null) EmitTableDefinitionBody(stmt.Definition);

        // ON / TEXTIMAGE_ON / FILESTREAM_ON each on their own line at column 0.
        if (stmt.OnFileGroupOrPartitionScheme != null)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("ON");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.OnFileGroupOrPartitionScheme, out var fgText);
            _emitter.Write((fgText ?? string.Empty).Trim());
        }
        if (stmt.TextImageOn != null)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("TEXTIMAGE_ON");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.TextImageOn, out var tiText);
            _emitter.Write((tiText ?? string.Empty).Trim());
        }
        if (stmt.FileStreamOn != null)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("FILESTREAM_ON");
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.FileStreamOn, out var fsText);
            _emitter.Write((fsText ?? string.Empty).Trim());
        }

        // Table-level WITH (...) options — MEMORY_OPTIMIZED, SYSTEM_VERSIONING, DURABILITY, etc.
        // Each table option is structured (e.g. SystemVersioningTableOption with HistoryTable);
        // the generator handles the inner shape correctly.
        if (stmt.Options != null && stmt.Options.Count > 0)
        {
            _emitter.NewLine();
            _emitter.WriteKeyword("WITH");
            _emitter.Write(" (");
            for (int i = 0; i < stmt.Options.Count; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _generator.GenerateScript(stmt.Options[i], out var optText);
                _emitter.Write((optText ?? string.Empty).Trim());
            }
            _emitter.Write(")");
        }

        _emitter.NewLine();
    }

    // 4d-v: parenthesised body of TableDefinition. Reused by CreateTable, AlterTable ADD (multi),
    // and the multi-stmt TVF backfill in EmitFunctionBody.
    private void EmitTableDefinitionBody(TableDefinition def)
    {
        _emitter.NewLine();
        _emitter.Write("(");
        _emitter.NewLine();
        using (_emitter.Indent())
        {
            bool first = true;
            if (def.ColumnDefinitions != null)
            {
                foreach (var col in def.ColumnDefinitions)
                {
                    if (!first) { _emitter.Write(","); _emitter.NewLine(); }
                    EmitColumnDefinition(col);
                    first = false;
                }
            }
            if (def.TableConstraints != null)
            {
                foreach (var con in def.TableConstraints)
                {
                    if (!first) { _emitter.Write(","); _emitter.NewLine(); }
                    EmitConstraintDefinition(con);
                    first = false;
                }
            }
            if (def.Indexes != null)
            {
                foreach (var idx in def.Indexes)
                {
                    if (!first) { _emitter.Write(","); _emitter.NewLine(); }
                    _generator.GenerateScript(idx, out var idxText);
                    _emitter.Write((idxText ?? string.Empty).Trim());
                    first = false;
                }
            }
            // Temporal PERIOD FOR SYSTEM_TIME — generator-render as one line at column-indent.
            if (!first && HasSystemTimePeriod(def, out var period))
            {
                _emitter.Write(",");
                _emitter.NewLine();
                _generator.GenerateScript(period!, out var pText);
                _emitter.Write((pText ?? string.Empty).Trim());
            }
        }
        _emitter.NewLine();
        _emitter.Write(")");
    }

    private static bool HasSystemTimePeriod(TableDefinition def, out TSqlFragment? period)
    {
        // Probed: TableDefinition.SystemTimePeriod : SystemTimePeriodDefinition (nullable).
        var prop = def.GetType().GetProperty("SystemTimePeriod");
        period = prop?.GetValue(def) as TSqlFragment;
        return period != null;
    }

    private void EmitColumnDefinition(ColumnDefinition col)
    {
        // Generator-render the full column. Per probe: identifier + type + collation +
        // identity + nullable + default + computed-AS + inline-constraints + inline-INDEX
        // all render correctly as a single line via Sql170ScriptGenerator.
        _generator.GenerateScript(col, out var text);
        // Generator emits a leading newline + indentation in some contexts; trim to a single line.
        _emitter.Write((text ?? string.Empty).Trim());
    }

    private void EmitConstraintDefinition(ConstraintDefinition con)
    {
        // UniqueConstraint (PK / UQ) is the only constraint that carries WITH options + ON
        // filegroup. D1 option C: inline WITH-options + ON if total fits MaxLineLength (with the
        // header indent factored in); else WITH-options wrap one-per-line at +2*IndentSize and
        // ON trails on its own line at +IndentSize.
        if (con is UniqueConstraintDefinition uq)
        {
            EmitUniqueConstraintDefinition(uq);
            return;
        }
        // FK / Check / Default — no per-constraint WITH/ON tail. Generator handles wholesale.
        _generator.GenerateScript(con, out var text);
        _emitter.Write((text ?? string.Empty).Trim());
    }

    private void EmitUniqueConstraintDefinition(UniqueConstraintDefinition uq)
    {
        // Header: [CONSTRAINT name] PRIMARY KEY|UNIQUE [CLUSTERED|NONCLUSTERED] (cols)
        var header = new StringBuilder();
        if (uq.ConstraintIdentifier != null)
        {
            _generator.GenerateScript(uq.ConstraintIdentifier, out var idText);
            header.Append("CONSTRAINT ").Append((idText ?? string.Empty).Trim()).Append(' ');
        }
        header.Append(uq.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE");
        if (uq.IndexType != null)
        {
            header.Append(uq.IndexType.IndexTypeKind == IndexTypeKind.Clustered ? " CLUSTERED" : " NONCLUSTERED");
        }
        else if (uq.Clustered == true)
        {
            header.Append(" CLUSTERED");
        }
        header.Append(" (");
        for (int i = 0; i < uq.Columns.Count; i++)
        {
            if (i > 0) header.Append(", ");
            _generator.GenerateScript(uq.Columns[i], out var cText);
            header.Append((cText ?? string.Empty).Trim());
        }
        header.Append(')');

        _emitter.Write(header.ToString());

        var hasOpts = uq.IndexOptions != null && uq.IndexOptions.Count > 0;
        var hasFg = uq.OnFileGroupOrPartitionScheme != null;
        if (!hasOpts && !hasFg) return;

        // Render WITH options + ON filegroup as inline-or-wrap.
        string[]? optTexts = null;
        if (hasOpts)
        {
            optTexts = new string[uq.IndexOptions.Count];
            for (int i = 0; i < uq.IndexOptions.Count; i++)
            {
                _generator.GenerateScript(uq.IndexOptions[i], out var t);
                optTexts[i] = (t ?? string.Empty).Trim();
            }
        }
        string fgText = string.Empty;
        if (hasFg)
        {
            _generator.GenerateScript(uq.OnFileGroupOrPartitionScheme, out var t);
            fgText = (t ?? string.Empty).Trim();
        }

        // Compute inline length: header is already on the line at currentIndent.
        int currentIndent = _emitter.IndentLevel * _options.IndentSize;
        int inlineExtra = 0;
        if (hasOpts)
        {
            inlineExtra += " WITH (".Length + 1; // " WITH (" + ")"
            for (int i = 0; i < optTexts!.Length; i++) inlineExtra += optTexts[i].Length;
            inlineExtra += (optTexts.Length - 1) * 2; // ", " separators
        }
        if (hasFg) inlineExtra += " ON ".Length + fgText.Length;
        bool inlineFits = currentIndent + header.Length + inlineExtra <= _options.MaxLineLength;

        if (inlineFits)
        {
            if (hasOpts)
            {
                _emitter.Write(" ");
                _emitter.WriteKeyword("WITH");
                _emitter.Write(" (");
                for (int i = 0; i < optTexts!.Length; i++)
                {
                    if (i > 0) _emitter.Write(", ");
                    _emitter.Write(optTexts[i]);
                }
                _emitter.Write(")");
            }
            if (hasFg)
            {
                _emitter.Write(" ");
                _emitter.WriteKeyword("ON");
                _emitter.Write(" ");
                _emitter.Write(fgText);
            }
            return;
        }

        // Wrapped form: WITH at +IndentSize, options at +2*IndentSize one per line, ON at +IndentSize.
        using (_emitter.Indent())
        {
            if (hasOpts)
            {
                _emitter.NewLine();
                _emitter.WriteKeyword("WITH");
                _emitter.Write(" (");
                _emitter.NewLine();
                using (_emitter.Indent())
                {
                    for (int i = 0; i < optTexts!.Length; i++)
                    {
                        if (i > 0) { _emitter.Write(","); _emitter.NewLine(); }
                        _emitter.Write(optTexts[i]);
                    }
                }
                _emitter.NewLine();
                _emitter.Write(")");
            }
            if (hasFg)
            {
                _emitter.NewLine();
                _emitter.WriteKeyword("ON");
                _emitter.Write(" ");
                _emitter.Write(fgText);
            }
        }
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

    // 4e-iii: DECLARE @var <type> [= <init>][, ...]. Drops the generator-injected `AS` keyword
    // (source-faithful + corpus-matching). Multi-var wrap shape: when the inline form would
    // exceed MaxLineLength * 2/3 (same threshold as procedure parameters / SELECT lists),
    // DECLARE sits alone on its line and each declaration lands at +IndentSize, comma-trailing.
    public override void ExplicitVisit(DeclareVariableStatement stmt)
    {
        _emitter.WriteKeyword("DECLARE");

        if (stmt.Declarations == null || stmt.Declarations.Count == 0) return;

        // Pre-render each declaration once. RenderDeclarationText is the same source used for
        // both wrap measurement and emission — single source of truth, single generator round-trip.
        var rendered = new string[stmt.Declarations.Count];
        int totalLen = 0;
        for (int i = 0; i < stmt.Declarations.Count; i++)
        {
            rendered[i] = RenderDeclarationText(stmt.Declarations[i]);
            totalLen += rendered[i].Length;
        }
        totalLen += (stmt.Declarations.Count - 1) * 2;                      // ", " separators

        bool wrap = totalLen > _options.MaxLineLength * 2 / 3;

        if (wrap)
        {
            _emitter.NewLine();
            using (_emitter.Indent())
            {
                for (int i = 0; i < rendered.Length; i++)
                {
                    if (i > 0) { _emitter.Write(","); _emitter.NewLine(); }
                    _emitter.Write(rendered[i]);
                }
            }
        }
        else
        {
            _emitter.Write(" ");
            for (int i = 0; i < rendered.Length; i++)
            {
                if (i > 0) _emitter.Write(", ");
                _emitter.Write(rendered[i]);
            }
        }
        if (!_emitter.AtLineStart) _emitter.NewLine();                      // statement convention: end on fresh line
    }

    private string RenderDeclarationText(DeclareVariableElement d)
    {
        var sb = new StringBuilder();
        sb.Append(d.VariableName.Value);
        sb.Append(' ');
        _generator.GenerateScript(d.DataType, out var typeText);
        sb.Append((typeText ?? string.Empty).Trim());
        if (d.Value != null)
        {
            sb.Append(" = ");
            _generator.GenerateScript(d.Value, out var valText);
            sb.Append((valText ?? string.Empty).Trim());
        }
        return sb.ToString();
    }

    // 4e-iii: DECLARE @t TABLE (...). Reuses EmitTableDefinitionBody so the column DDL renders
    // with the same per-column wrap rules as CREATE TABLE / multi-stmt TVF (4d-v) — fixes the
    // generator's column-alignment padding artifact (`id   INT           ,`).
    public override void ExplicitVisit(DeclareTableVariableStatement stmt)
    {
        var body = stmt.Body;
        _emitter.WriteKeyword("DECLARE");
        _emitter.Write(" ");
        _emitter.Write(body.VariableName.Value);
        _emitter.Write(" ");
        _emitter.WriteKeyword("TABLE");
        if (body.Definition != null) EmitTableDefinitionBody(body.Definition);
        if (!_emitter.AtLineStart) _emitter.NewLine();                      // statement convention: end on fresh line
    }

    // 4e-iii: ROLLBACK [TRANSACTION [name]]. The generator drops the keyword entirely
    // (`ROLLBACK TRAN;` → `ROLLBACK`) — silent loss of intent. Re-emit explicitly so the keyword
    // survives and the form is symmetric with BEGIN / COMMIT / SAVE TRANSACTION (which the
    // generator handles cleanly and we leave as-is). The AST does not preserve the source
    // distinction between TRAN and TRANSACTION, so always emit the long form.
    public override void ExplicitVisit(RollbackTransactionStatement stmt)
    {
        _emitter.WriteKeyword("ROLLBACK TRANSACTION");
        if (stmt.Name != null)
        {
            _emitter.Write(" ");
            _generator.GenerateScript(stmt.Name, out var nameText);
            _emitter.Write((nameText ?? string.Empty).Trim());
        }
        if (!_emitter.AtLineStart) _emitter.NewLine();                      // statement convention: end on fresh line
    }

    // 4f: only the ScalarSubquery RHS case gets break-to-block; everything else (literals,
    // expressions, CURSOR, NEXT VALUE FOR, parens-around-scalar) passes through to the
    // generator. The break shape mirrors ScalarSubquery (4b-ii) — single pattern across the
    // formatter for "subquery in scalar position": `(` on its own line, body indented, `)`
    // on its own line.
    public override void ExplicitVisit(SetVariableStatement stmt)
    {
        if (stmt.Expression is not ScalarSubquery sq)
        {
            EmitGeneratorRaw(stmt);
            return;
        }

        _emitter.WriteKeyword("SET");
        _emitter.Write(" ");
        _emitter.Write(stmt.Variable.Name);
        _emitter.Write(" ");
        _emitter.Write(AssignmentOperator(stmt.AssignmentKind));
        _emitter.Write(" ");
        ExplicitVisit(sq);
        if (!_emitter.AtLineStart) _emitter.NewLine();                      // statement convention: end on fresh line
    }

    private static string AssignmentOperator(AssignmentKind kind) => kind switch
    {
        AssignmentKind.Equals => "=",
        AssignmentKind.AddEquals => "+=",
        AssignmentKind.SubtractEquals => "-=",
        AssignmentKind.MultiplyEquals => "*=",
        AssignmentKind.DivideEquals => "/=",
        AssignmentKind.ModEquals => "%=",
        AssignmentKind.BitwiseAndEquals => "&=",
        AssignmentKind.BitwiseOrEquals => "|=",
        AssignmentKind.BitwiseXorEquals => "^=",
        _ => "=",
    };

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
        if (fragment is CreateViewStatement cv) { ExplicitVisit(cv); return; }
        if (fragment is AlterViewStatement av) { ExplicitVisit(av); return; }
        if (fragment is CreateOrAlterViewStatement coav) { ExplicitVisit(coav); return; }
        if (fragment is CreateFunctionStatement cf) { ExplicitVisit(cf); return; }
        if (fragment is AlterFunctionStatement af) { ExplicitVisit(af); return; }
        if (fragment is CreateOrAlterFunctionStatement coaf) { ExplicitVisit(coaf); return; }
        if (fragment is CreateTriggerStatement ct) { ExplicitVisit(ct); return; }
        if (fragment is AlterTriggerStatement at) { ExplicitVisit(at); return; }
        if (fragment is CreateOrAlterTriggerStatement coat) { ExplicitVisit(coat); return; }
        if (fragment is CreateTableStatement ctab) { ExplicitVisit(ctab); return; }
        if (fragment is AlterTableAddTableElementStatement atadd) { ExplicitVisit(atadd); return; }
        if (fragment is AlterTableDropTableElementStatement atdrop) { ExplicitVisit(atdrop); return; }
        if (fragment is AlterTableAlterColumnStatement atalt) { ExplicitVisit(atalt); return; }
        if (fragment is AlterTableSwitchStatement atsw) { ExplicitVisit(atsw); return; }
        if (fragment is AlterTableTriggerModificationStatement attm) { ExplicitVisit(attm); return; }
        if (fragment is AlterTableConstraintModificationStatement atcm) { ExplicitVisit(atcm); return; }
        if (fragment is BeginEndBlockStatement beb) { ExplicitVisit(beb); return; }
        if (fragment is TryCatchStatement tc) { ExplicitVisit(tc); return; }
        if (fragment is IfStatement ifs) { ExplicitVisit(ifs); return; }
        if (fragment is WhileStatement ws) { ExplicitVisit(ws); return; }
        if (fragment is DeclareVariableStatement dvs) { ExplicitVisit(dvs); return; }
        if (fragment is DeclareTableVariableStatement dtv) { ExplicitVisit(dtv); return; }
        if (fragment is RollbackTransactionStatement rts) { ExplicitVisit(rts); return; }
        if (fragment is PivotedTableReference pvt) { ExplicitVisit(pvt); return; }
        if (fragment is UnpivotedTableReference upvt) { ExplicitVisit(upvt); return; }
        if (fragment is SetVariableStatement svs) { ExplicitVisit(svs); return; }

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
