using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class JoinAndCteFormattingTests
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
    public void Format_InnerJoin_WithWhere_NoTrailingClauseDrop()
    {
        // 4b-iii-a deliverable: the WHERE clause must survive. Prior to this slice, the
        // QuerySpec-level generator fallback for JOINed QuerySpecs silently dropped trailing
        // clauses. Shape captured from actual emitter output.
        var input = "SELECT * FROM t INNER JOIN u ON t.id = u.t_id WHERE x = 1";
        var output = Fmt(input);

        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "       INNER JOIN u ON t.id = u.t_id\n" +
            " WHERE x = 1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LeftOuterJoinChain_Stacks()
    {
        var input = "SELECT * FROM t LEFT OUTER JOIN u ON t.id = u.t_id LEFT OUTER JOIN v ON u.id = v.u_id";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "       LEFT OUTER JOIN u ON t.id = u.t_id\n" +
            "       LEFT OUTER JOIN v ON u.id = v.u_id\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CrossJoin_NoOn()
    {
        var input = "SELECT * FROM t CROSS JOIN u";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "       CROSS JOIN u\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DerivedTableInFrom_BreaksToBlock()
    {
        var input = "SELECT * FROM (SELECT a, b FROM t) AS sub";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM (\n" +
            "           SELECT a, b\n" +
            "             FROM t\n" +
            "       ) AS sub\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CrossApply_WithSubquery()
    {
        // Non-TOP inner — TOP is out of scope for 4b-iii-a (trips TopRowFilter fallback).
        var input = "SELECT * FROM t CROSS APPLY (SELECT a FROM u WHERE u.t_id = t.id) AS x";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "       CROSS APPLY (\n" +
            "           SELECT a\n" +
            "             FROM u\n" +
            "            WHERE u.t_id = t.id\n" +
            "       ) AS x\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LongOn_BreaksToOwnLine()
    {
        // Simple comparison (no AND/OR — AND/OR multi-line is 4b-iv). Long identifier names
        // push the rendered condition past the heuristic threshold so the break-ON branch fires.
        var input = "SELECT * FROM t INNER JOIN u ON t.some_very_long_column_name_abcdefghij = u.some_other_long_column_name_qrstuvwxyz_and_more";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "       INNER JOIN u\n" +
            "           ON t.some_very_long_column_name_abcdefghij = u.some_other_long_column_name_qrstuvwxyz_and_more\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ShortSelectList_StaysSingleLine()
    {
        // Regression: don't wrap short lists. 4b-iii-b wrap threshold = MaxLineLength * 2/3 = 80.
        var input = "SELECT a, b, c FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT a, b, c\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LongSelectList_Wraps()
    {
        // Long-enough identifiers push the rendered list past the 80-char threshold. Wraps to
        // one per line with trailing commas; continuation lines land at the body column (= 7,
        // same as a SELECT scope's maxKw + 1 = 6+1).
        var input = "SELECT long_column_name_one, long_column_name_two, long_column_name_three, long_column_name_four FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT long_column_name_one,\n" +
            "       long_column_name_two,\n" +
            "       long_column_name_three,\n" +
            "       long_column_name_four\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LongGroupBy_Wraps()
    {
        // GROUP BY is 8 chars → maxKw alignment kicks in if outer scope has wider keyword
        // (none here — SELECT=6, FROM=4, GROUP BY=8, so maxKw=8). Body col = 9. Wrap threshold
        // = 80 chars of comma-joined list.
        var input = "SELECT count(*) FROM t GROUP BY long_column_name_one, long_column_name_two, long_column_name_three, long_column_name_four";
        var output = Fmt(input);
        var expected =
            "  SELECT count(*)\n" +
            "    FROM t\n" +
            "GROUP BY long_column_name_one,\n" +
            "         long_column_name_two,\n" +
            "         long_column_name_three,\n" +
            "         long_column_name_four\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LongOrderBy_Wraps()
    {
        // ORDER BY is 8 chars, same width as SELECT+2 so maxKw=8, body col = 9. Wrap threshold
        // = MaxLineLength * 2/3 = 80.
        var input = "SELECT * FROM t ORDER BY long_column_name_one DESC, long_column_name_two ASC, long_column_name_three DESC, long_column_name_four ASC";
        var output = Fmt(input);
        var expected =
            "  SELECT *\n" +
            "    FROM t\n" +
            "ORDER BY long_column_name_one DESC,\n" +
            "         long_column_name_two ASC,\n" +
            "         long_column_name_three DESC,\n" +
            "         long_column_name_four ASC\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_LongOldStyleFromList_Wraps()
    {
        // Old-style implicit join: FROM a, b, c. Wraps when long enough. FROM=4, body col = 7.
        var input = "SELECT * FROM long_table_name_one, long_table_name_two, long_table_name_three, long_table_name_four WHERE 1 = 1";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM long_table_name_one,\n" +
            "       long_table_name_two,\n" +
            "       long_table_name_three,\n" +
            "       long_table_name_four\n" +
            " WHERE 1 = 1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SingleCte_FormatsCleanly()
    {
        // D2 flat indent: WITH at col 0, body at col 4, closing ) at col 0.
        var input = "WITH cte1 AS (SELECT a, b FROM t) SELECT * FROM cte1";
        var output = Fmt(input);
        var expected =
            "WITH cte1 AS (\n" +
            "    SELECT a, b\n" +
            "      FROM t\n" +
            ")\n" +
            "SELECT *\n" +
            "  FROM cte1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ChainedCtes_StackVertically()
    {
        // Trailing-comma style (D3): the "," lands on cte1's closing `)` line, next CTE on a
        // fresh line at col 0. Inner QuerySpecs have their own local clause scopes.
        var input = "WITH cte1 AS (SELECT a FROM t), cte2 AS (SELECT b FROM u) SELECT * FROM cte1 INNER JOIN cte2 ON cte1.a = cte2.b";
        var output = Fmt(input);
        var expected =
            "WITH cte1 AS (\n" +
            "    SELECT a\n" +
            "      FROM t\n" +
            "),\n" +
            "cte2 AS (\n" +
            "    SELECT b\n" +
            "      FROM u\n" +
            ")\n" +
            "SELECT *\n" +
            "  FROM cte1\n" +
            "       INNER JOIN cte2 ON cte1.a = cte2.b\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CteWithLeftJoinDerivedTable_InnerIndentsRelativeToParen()
    {
        // 4c step 0: post-4b-iv bug fix. CTE body contains `LEFT JOIN (SELECT ...) AS alias ON ...`.
        // Before fix: inner SELECT at col 19 (parent outerIndent double-counted through the
        // nested-pop path). After fix (strip parent.captured * IndentSize from injected lines
        // in SqlEmitter.EndClauseScope): inner SELECT at col 15 = outer FROM body col (11) + 4.
        var input = "WITH cte AS (SELECT a.id FROM a LEFT JOIN (SELECT b.x FROM b WHERE b.y = 1) AS t ON a.id = t.x) SELECT * FROM cte";
        var output = Fmt(input);
        var expected =
            "WITH cte AS (\n" +
            "    SELECT a.id\n" +
            "      FROM a\n" +
            "           LEFT OUTER JOIN (\n" +
            "               SELECT b.x\n" +
            "                 FROM b\n" +
            "                WHERE b.y = 1\n" +
            "           ) AS t ON a.id = t.x\n" +
            ")\n" +
            "SELECT *\n" +
            "  FROM cte\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_JoinInsideSubquery_PreservesTrailingClauses()
    {
        // The harder regression (Known limitation #3 pre-4b-iii-a): JOINed QuerySpec inside a
        // subquery — no surrounding SelectStatement to fall back on. Inner WHERE must survive.
        var input = "SELECT * FROM a WHERE x IN (SELECT * FROM b INNER JOIN c ON b.id = c.b_id WHERE y = 1)";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            " WHERE x IN (\n" +
            "           SELECT *\n" +
            "             FROM b\n" +
            "                  INNER JOIN c ON b.id = c.b_id\n" +
            "            WHERE y = 1\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_QdtInJoin_WithInnerTop_Staircase()
    {
        // 4f-ii regression: LEFT JOIN (SELECT TOP N ...) alias ON ... — the inner QuerySpec
        // with TOP routes through the visitor's clause scope, producing staircase keywords
        // consistent with the surrounding query. Pre-4f-ii this rendered in generator-style
        // left-aligned (Style 2) with all inner keywords flush at one column.
        var input = "SELECT a.id FROM a LEFT JOIN (SELECT TOP 5 id, name FROM b WHERE active = 1) alias ON a.id = alias.id;";
        var output = Fmt(input);
        var expected =
            "SELECT a.id\n" +
            "  FROM a\n" +
            "       LEFT OUTER JOIN (\n" +
            "           SELECT TOP 5 id, name\n" +
            "             FROM b\n" +
            "            WHERE active = 1\n" +
            "       ) AS alias ON a.id = alias.id\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    // ===== Branch C — BBE-quirk fix =====
    // Multi-AND/OR ON conditions used to render via the EmitGeneratorRaw(bbe) bail (BBE outside a
    // clause scope), dropping subsequent AND/OR lines at column 1 — visible across the corpus
    // (e.g. Sorgu/Buyuk Kucuk Kasa Yeni.sql:34). Fix: when the search condition is a
    // BooleanBinaryExpression, break ON to its own line and open a synthetic clause scope so
    // AND/OR right-aligns with ON. Single-comparison ON keeps inline-or-long-break heuristic.

    [Fact]
    public void Format_InnerJoin_OnSingleAnd_BreaksToScope()
    {
        // Two-cond AND ON breaks to own line; ON / AND right-align in synthetic scope under
        // the JOIN body column. Pre-fix this rendered the second AND at col 1.
        var input = "SELECT * FROM a INNER JOIN b ON a.x = b.x AND a.y = b.y";
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            "       INNER JOIN b\n" +
            "        ON a.x = b.x\n" +
            "       AND a.y = b.y\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InnerJoin_OnTripleAnd_AllAndsAlign()
    {
        var input = "SELECT * FROM a INNER JOIN b ON a.x = b.x AND a.y = b.y AND a.z = b.z";
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            "       INNER JOIN b\n" +
            "        ON a.x = b.x\n" +
            "       AND a.y = b.y\n" +
            "       AND a.z = b.z\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_LeftJoin_OnMultiAnd_BreaksToScope()
    {
        // LEFT OUTER JOIN keyword is wider — confirms the JOIN body column is invariant under
        // join-keyword-width changes (the synthetic scope captures _indentLevel, not flush-column).
        var input = "SELECT * FROM a LEFT JOIN b ON a.x = b.x AND a.y = b.y AND a.z = b.z";
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            "       LEFT OUTER JOIN b\n" +
            "        ON a.x = b.x\n" +
            "       AND a.y = b.y\n" +
            "       AND a.z = b.z\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InnerJoin_OnMixedAndOr_AllOperatorsAlign()
    {
        var input = "SELECT * FROM a INNER JOIN b ON a.x = b.x AND a.y = b.y OR a.z = b.z";
        var output = Fmt(input);
        // ScriptDom right-associative parse: AND binds tighter than OR, so the BBE tree is
        // OR(AND(x, y), z=z). Top-level operator emitted at the synthetic scope is OR.
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            "       INNER JOIN b\n" +
            "        ON a.x = b.x\n" +
            "       AND a.y = b.y\n" +
            "        OR a.z = b.z\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_UpdateFromJoin_MultiAndOn_RendersStaircase()
    {
        // Corpus shape from Sorgu/Buyuk Kucuk Kasa Yeni.sql:31-41. Multi-AND ON inside an
        // UPDATE FROM JOIN. Outer UPDATE/SET/FROM/WHERE scope, plus inner subquery scope,
        // plus synthetic ON scope — three nested scopes, all should staircase consistently.
        var input = "UPDATE t SET t.k = j.k FROM #temp t JOIN (SELECT a, b, COUNT(*) AS k FROM x WHERE q = '1' AND w = '2' GROUP BY a, b) j ON j.a = t.OrderNumber AND j.b = t.ShipDate";
        var output = Fmt(input);
        var expected =
            "UPDATE t\n" +
            "   SET t.k = j.k\n" +
            "  FROM #temp AS t\n" +
            "       INNER JOIN (\n" +
            "             SELECT a, b, COUNT(*) AS k\n" +
            "               FROM x\n" +
            "              WHERE q = '1'\n" +
            "                AND w = '2'\n" +
            "           GROUP BY a, b\n" +
            "       ) AS j\n" +
            "        ON j.a = t.OrderNumber\n" +
            "       AND j.b = t.ShipDate\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_InnerJoin_OnSingleComparison_StaysInline()
    {
        // Regression guard: single-comparison ON is NOT a BooleanBinaryExpression, so the fix
        // doesn't fire — it keeps the inline-or-long-break heuristic. This locks the existing
        // 4b-iii-a behavior (cf. Format_InnerJoin_WithWhere_NoTrailingClauseDrop).
        var input = "SELECT * FROM a INNER JOIN b ON a.x = b.x";
        var expected =
            "SELECT *\n" +
            "  FROM a\n" +
            "       INNER JOIN b ON a.x = b.x\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }
}
