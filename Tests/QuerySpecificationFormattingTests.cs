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
}
