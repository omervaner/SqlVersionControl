using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class QuerySpecificationFormattingTests
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
    public void Format_SelectOnly_AlignsSingleKeyword()
    {
        Assert.Equal("SELECT 1\n", Fmt("SELECT 1"));
    }

    [Fact]
    public void Format_SelectFrom_AlignsTwoKeywords()
    {
        Assert.Equal(
            "SELECT *\n" +
            "  FROM dbo.Employees\n",
            Fmt("SELECT * FROM dbo.Employees"));
    }

    [Fact]
    public void Format_SelectFromWhere_AlignsThreeKeywords()
    {
        Assert.Equal(
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE x = 1\n",
            Fmt("SELECT * FROM t WHERE x = 1"));
    }

    [Fact]
    public void Format_FullClauseSet_AlignsAllKeywords()
    {
        var input = "SELECT a FROM t WHERE x = 1 GROUP BY y HAVING COUNT(*) > 1 ORDER BY z";
        var output = Fmt(input);

        // maxKw = 8 ("GROUP BY" / "ORDER BY"). SELECT padded by 2, FROM by 4, WHERE by 3, HAVING by 2.
        Assert.Contains("  SELECT a\n", output);
        Assert.Contains("    FROM t\n", output);
        Assert.Contains("   WHERE x = 1\n", output);
        Assert.Contains("GROUP BY y\n", output);
        Assert.Contains("  HAVING", output);
        Assert.Contains("ORDER BY z\n", output);

        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_WhereWithSubquery_OuterAlignsInnerMayNot()
    {
        // The screenshot query — deliverable proof for 4b-i. Outer alignment asserted;
        // inner subquery layout is 4b-ii's concern and not asserted here.
        var input = "SELECT * FROM [dbo].[Employees] WHERE id IN (SELECT * FROM [dbo].[Employees])";
        var output = Fmt(input);

        Assert.StartsWith("SELECT *\n", output);
        Assert.Contains("  FROM [dbo].[Employees]\n", output);
        Assert.Contains(" WHERE id IN ", output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_MultiColumn_OuterStillAligns()
    {
        var output = Fmt("SELECT col1, col2, col3 FROM t");
        Assert.Equal(
            "SELECT col1, col2, col3\n" +
            "  FROM t\n",
            output);
    }

    [Fact]
    public void Format_ReParsesToSameStatementShape()
    {
        var input = "SELECT a, b FROM t WHERE x > 1 GROUP BY a, b HAVING SUM(x) > 10 ORDER BY a";
        var output = Fmt(input);

        var before = ReParse(input);
        var after = ReParse(output);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.Batches.Count, after!.Batches.Count);
        Assert.Equal(before.Batches[0].Statements.Count, after.Batches[0].Statements.Count);
        Assert.IsType<SelectStatement>(after.Batches[0].Statements[0]);
    }

    [Fact]
    public void Format_SelectWithParenthesizedQuery_PreservesAllClauses()
    {
        // 4d-ii bundled fix: when SelectStatement.QueryExpression is a QueryParenthesisExpression
        // (e.g. `CREATE VIEW v AS (SELECT ...)` real-corpus shape), the dispatcher unwraps the
        // parens and recurses on the inner QuerySpec. Pre-fix, the bare-QPE rendered through
        // EmitGeneratorRaw and the generator silently dropped WHERE / GROUP BY / HAVING / ORDER BY
        // (same root cause as the bare-QuerySpec quirk). The four trailing clauses below MUST
        // appear — the test would have caught the silent data loss the smoke surfaced.
        var input =
            "CREATE VIEW dbo.v AS (SELECT id, COUNT(*) AS c " +
            "FROM dbo.t " +
            "WHERE active = 1 " +
            "GROUP BY id " +
            "HAVING COUNT(*) > 1)";
        var output = Fmt(input);
        Assert.Contains("SELECT", output);
        Assert.Contains("FROM dbo.t", output);
        Assert.Contains("WHERE active = 1", output);
        Assert.Contains("GROUP BY id", output);
        Assert.Contains("HAVING COUNT", output);
        Assert.NotNull(ReParse(output));
    }

    // 4f-ii: TOP renders inline within the SELECT body — `TOP <expr> [PERCENT] [WITH TIES] ` —
    // before SelectElements. Niche-feature trip-flag retired; QuerySpec opens its own clause
    // scope and right-aligns all keywords (including OFFSET / FOR) staircase-consistently.

    [Fact]
    public void Format_SelectTop_IntegerLiteral()
    {
        var input = "SELECT TOP 5 a, b FROM t WHERE x = 1;";
        var output = Fmt(input);
        var expected =
            "SELECT TOP 5 a, b\n" +
            "  FROM t\n" +
            " WHERE x = 1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SelectTop_Parenthesized()
    {
        var input = "SELECT TOP (10) * FROM t;";
        var output = Fmt(input);
        var expected =
            "SELECT TOP (10) *\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SelectTop_PercentWithTies()
    {
        var input = "SELECT TOP (10) PERCENT WITH TIES * FROM t ORDER BY y;";
        var output = Fmt(input);
        var expected =
            "  SELECT TOP (10) PERCENT WITH TIES *\n" +
            "    FROM t\n" +
            "ORDER BY y\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SelectTop_VariableExpression()
    {
        var input = "SELECT TOP (@n) * FROM t;";
        var output = Fmt(input);
        var expected =
            "SELECT TOP (@n) *\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Offset_OnlyOffset()
    {
        var input = "SELECT * FROM t ORDER BY y OFFSET 10 ROWS;";
        var output = Fmt(input);
        // maxKw = 8 (ORDER BY). OFFSET pads by 2.
        var expected =
            "  SELECT *\n" +
            "    FROM t\n" +
            "ORDER BY y\n" +
            "  OFFSET 10 ROWS\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Offset_OffsetFetch()
    {
        var input = "SELECT * FROM t ORDER BY y OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY;";
        var output = Fmt(input);
        // FETCH NEXT inline after OFFSET on the same body line.
        var expected =
            "  SELECT *\n" +
            "    FROM t\n" +
            "ORDER BY y\n" +
            "  OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ForXml_PathEmpty()
    {
        var input = "SELECT col FROM t WHERE x = 1 FOR XML PATH('');";
        var output = Fmt(input);
        // Generator-rendered ForClause body keeps the `PATH ('')` space (generator-ism — see
        // FORMATTER-INTERNALS § Known limitations).
        var expected =
            "SELECT col\n" +
            "  FROM t\n" +
            " WHERE x = 1\n" +
            "   FOR XML PATH ('')\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ForJson_PathRootInclude()
    {
        var input = "SELECT * FROM t FOR JSON PATH, ROOT('data'), INCLUDE_NULL_VALUES;";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "   FOR JSON PATH, ROOT ('data'), INCLUDE_NULL_VALUES\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ForBrowse()
    {
        var input = "SELECT * FROM t FOR BROWSE;";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            "   FOR BROWSE\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_StuffForXmlPath_Corpus_KnownGeneratorPassthrough()
    {
        // Captures-known-limitation (memory feedback_capture_test_labels): when a ScalarSubquery
        // sits inside a function-call argument (here STUFF), the function call is generator-
        // rendered as one expression — the inner subquery never reaches the visitor's
        // ScalarSubquery dispatch. Result: inner FROM / WHERE / FOR keep the generator's
        // left-aligned-keyword style ("FROM   ", "WHERE  ", "FOR    "), regardless of 4f-ii.
        // 4f-ii fixed Style 2 leaks for visitor-dispatched ScalarSubqueries; function-arg
        // subqueries are a separate slice. Test locks current shape to flag any silent change.
        var input = "SELECT STUFF((SELECT ',' + isnull(tu.upc, itm.upc) FROM t_item_upc tu (NOLOCK) WHERE sto.wh_id = tu.wh_id FOR XML PATH('')), 1, 1, '') upc FROM sto;";
        var output = Fmt(input);
        var expected =
            "SELECT STUFF((SELECT ',' + isnull(tu.upc, itm.upc)\n" +
            "       FROM   t_item_upc AS tu WITH (NOLOCK)\n" +
            "       WHERE  sto.wh_id = tu.wh_id\n" +
            "       FOR    XML PATH ('')), 1, 1, '') AS upc\n" +
            "  FROM sto\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
