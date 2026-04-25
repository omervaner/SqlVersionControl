using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class CaseAndUnionFormattingTests
{
    private static string Fmt(string input) => ScriptDomFormatter.Format(input, new FormatterOptions());

    private static TSqlScript? ReParse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var result = parser.Parse(reader, out var errors) as TSqlScript;
        return (errors == null || errors.Count == 0) ? result : null;
    }

    [Fact]
    public void Format_SearchedCase_InSelectElement_Multiline()
    {
        // CASE on header line at SELECT body col 7. WHEN/ELSE at +IndentSize = col 11. END
        // returns to col 7 (CASE column). Per D1.
        var input = "SELECT CASE WHEN x = 1 THEN 'a' WHEN x = 2 THEN 'b' ELSE 'c' END AS category, other_col FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT CASE\n" +
            "           WHEN x = 1 THEN 'a'\n" +
            "           WHEN x = 2 THEN 'b'\n" +
            "           ELSE 'c'\n" +
            "       END AS category, other_col\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SimpleCase_InSelectElement_Multiline()
    {
        // CASE <input_expr> on header line; WHEN compares against input. ELSE / END align like searched.
        var input = "SELECT CASE status WHEN 1 THEN 'active' WHEN 2 THEN 'pending' ELSE 'unknown' END FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT CASE status\n" +
            "           WHEN 1 THEN 'active'\n" +
            "           WHEN 2 THEN 'pending'\n" +
            "           ELSE 'unknown'\n" +
            "       END\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SearchedCase_MultiWhen_WithAndInCondition_StaysMultiline()
    {
        // Mirrors the test_sproc shape that surfaced 4c-iii's diagnosis: a multi-branch CASE
        // whose WHEN conditions include an AND. Locks that the visitor emits each WHEN on its
        // own line (not generator's compact-then-wrap-at-col-70). Bug only reproduces inside a
        // CREATE PROCEDURE wrapper today (4d's job to remove that fallback) — this fact protects
        // the non-wrapped path from regressing while 4d lands.
        var input =
            "SELECT p.pick_id, CASE WHEN o.order_type = 'RUSH' THEN 1 " +
            "WHEN o.order_type = 'B2B' AND p.priority < 5 THEN 2 " +
            "WHEN o.order_type = 'RETAIL' THEN 3 ELSE 9 END AS effective_priority " +
            "FROM dbo.t_pick AS p INNER JOIN dbo.t_order AS o ON o.order_id = p.order_id";
        var output = Fmt(input);
        var expected =
            "SELECT p.pick_id, CASE\n" +
            "           WHEN o.order_type = 'RUSH' THEN 1\n" +
            "           WHEN o.order_type = 'B2B' AND p.priority < 5 THEN 2\n" +
            "           WHEN o.order_type = 'RETAIL' THEN 3\n" +
            "           ELSE 9\n" +
            "       END AS effective_priority\n" +
            "  FROM dbo.t_pick AS p\n" +
            "       INNER JOIN dbo.t_order AS o ON o.order_id = p.order_id\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_NestedCase_RecursesCleanly()
    {
        // Inner CASE inside outer THEN body. _indentLevel × IndentSize drives the per-level pad,
        // so inner WHENs sit deeper than outer's (col 15 vs col 11). Inner END pad mirrors the
        // outer level when END emits, so inner END lands at col 11 (outer's WHEN/ELSE column) —
        // a clean closing brace for the contained block, not aligned with the inner CASE keyword
        // itself. Locked via capture-then-update (D9).
        var input = "SELECT CASE WHEN outer_cond = 1 THEN CASE WHEN inner_cond = 1 THEN 'a' ELSE 'b' END ELSE 'c' END FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT CASE\n" +
            "           WHEN outer_cond = 1 THEN CASE\n" +
            "               WHEN inner_cond = 1 THEN 'a'\n" +
            "               ELSE 'b'\n" +
            "           END\n" +
            "           ELSE 'c'\n" +
            "       END\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CaseInWhereComparison_DispatchesCorrectly()
    {
        // CASE on one side of a comparison routes through EmitComparisonOperandScaffold →
        // DispatchCaseExpression. ComparisonHasMultilineSide flips the dispatcher.
        var input = "SELECT * FROM t WHERE CASE WHEN a = 1 THEN 'x' ELSE 'y' END = 'x'";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE CASE\n" +
            "           WHEN a = 1 THEN 'x'\n" +
            "           ELSE 'y'\n" +
            "       END = 'x'\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_UnionAll_TopLevel_StacksVertically()
    {
        // UNION ALL at statement indent (col 0). No clause scope; arm QuerySpecs each open
        // their own. Per D2.
        var input = "SELECT a FROM t1 UNION ALL SELECT b FROM t2";
        var output = Fmt(input);
        var expected =
            "SELECT a\n" +
            "  FROM t1\n" +
            "UNION ALL\n" +
            "SELECT b\n" +
            "  FROM t2\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_UnionThreeArms_ChainsCleanly()
    {
        // BQE(BQE(a,b), c) — inner BQE recurses naturally via EmitSubqueryQueryExpression's
        // BQE branch. Per D8.
        var input = "SELECT a FROM t1 UNION ALL SELECT b FROM t2 UNION ALL SELECT c FROM t3";
        var output = Fmt(input);
        var expected =
            "SELECT a\n" +
            "  FROM t1\n" +
            "UNION ALL\n" +
            "SELECT b\n" +
            "  FROM t2\n" +
            "UNION ALL\n" +
            "SELECT c\n" +
            "  FROM t3\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_UnionInsideSubquery_IndentsCorrectly()
    {
        // Resolves Known limitation #2 (subquery whose QueryExpression is BinaryQueryExpression).
        // EmitSubqueryQueryExpression's BQE branch dispatches; arm QuerySpecs each open their
        // own clause scope inside the subquery's +IndentSize block. UNION ALL sits left of the
        // inner SELECTs (col 7 vs col 11) — correct per D2.
        var input = "SELECT * FROM t WHERE id IN (SELECT a FROM t1 UNION ALL SELECT b FROM t2)";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE id IN (\n" +
            "           SELECT a\n" +
            "             FROM t1\n" +
            "       UNION ALL\n" +
            "           SELECT b\n" +
            "             FROM t2\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_UnionWithTopLevelOrderBy_EmitsAtStatementIndent()
    {
        // Per D4: BinaryQueryExpression.OrderByClause (inherited from QueryExpression) emits in
        // its own one-clause scope at statement indent, after both arms. Single-keyword scope
        // produces no padding (maxKw = 8, but only one keyword line).
        var input = "SELECT a FROM t1 UNION ALL SELECT a FROM t2 ORDER BY a";
        var output = Fmt(input);
        var expected =
            "SELECT a\n" +
            "  FROM t1\n" +
            "UNION ALL\n" +
            "SELECT a\n" +
            "  FROM t2\n" +
            "ORDER BY a\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_WhereWithAnd_AndRightAligns()
    {
        // AND inside the WHERE clause scope right-aligns via WriteClauseKeyword (D3). maxKw=5
        // (WHERE), so AND lines pad to body col 6 with leading 2 spaces.
        var input = "SELECT * FROM t WHERE a = 1 AND b = 2";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a = 1\n" +
            "   AND b = 2\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_WhereWithOrAndPrecedence_RecursesByParseTree()
    {
        // a AND b OR c parses as BBE_OR(BBE_AND(a, b), c) — AND binds tighter than OR. Recursion
        // produces three lines: a / AND b / OR c. Each connector right-aligns with WHERE.
        var input = "SELECT * FROM t WHERE a = 1 AND b = 2 OR c = 3";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a = 1\n" +
            "   AND b = 2\n" +
            "    OR c = 3\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_WhereSubqueryUnderAnd_BreaksToBlock()
    {
        // Known limitation #1 fix. EmitSearchConditionBody recursing through BBE → operand routes
        // an InPredicate(subquery) through the visitor, which breaks to indented block.
        var input = "SELECT * FROM t WHERE a = 1 AND id IN (SELECT * FROM u)";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a = 1\n" +
            "   AND id IN (\n" +
            "           SELECT *\n" +
            "             FROM u\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DeeplyNestedAndOr_TerminatesWithoutStackOverflow()
    {
        // Defensive test (#14 from plan): 5 chained AND with one OR — exercises the
        // EmitSearchConditionBody → ExplicitVisit(BBE) → EmitSearchConditionBody recursion at
        // depth 6. If this stack-overflows, a depth guard goes on the visitor (see plan Risks).
        // Locks the multi-AND shape as a bonus.
        var input = "SELECT * FROM t WHERE a = 1 AND b = 2 AND c = 3 AND d = 4 AND e = 5 OR f = 6";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a = 1\n" +
            "   AND b = 2\n" +
            "   AND c = 3\n" +
            "   AND d = 4\n" +
            "   AND e = 5\n" +
            "    OR f = 6\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Intersect_FormatsLikeUnion()
    {
        var input = "SELECT a FROM t1 INTERSECT SELECT a FROM t2";
        var output = Fmt(input);
        var expected =
            "SELECT a\n" +
            "  FROM t1\n" +
            "INTERSECT\n" +
            "SELECT a\n" +
            "  FROM t2\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CaseWithoutElse_FormatsCleanly()
    {
        // No ELSE branch — END follows last WHEN directly, still at CASE column.
        var input = "SELECT CASE WHEN x = 1 THEN 'a' WHEN x = 2 THEN 'b' END FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT CASE\n" +
            "           WHEN x = 1 THEN 'a'\n" +
            "           WHEN x = 2 THEN 'b'\n" +
            "       END\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
