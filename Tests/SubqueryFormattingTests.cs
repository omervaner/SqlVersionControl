using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class SubqueryFormattingTests
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
    public void Format_WhereInSubquery_InnerAligns()
    {
        // The screenshot query — the deliverable for 4b-ii. Outer maxKw=6 (SELECT) → outer
        // body col 7. Inner indented one level (+IndentSize=4) inside the WHERE body, with
        // its own local maxKw=6 → inner SELECT at col 11 (7 + 4), inner FROM at col 13
        // (7 + 4 + 2 inner pad). Closing `)` lands at outer body col 7.
        var input = "SELECT * FROM [dbo].[Employees] WHERE id IN (SELECT * FROM [dbo].[Employees])";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM [dbo].[Employees]\n" +
            " WHERE id IN (\n" +
            "           SELECT *\n" +
            "             FROM [dbo].[Employees]\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_WhereExistsSubquery_InnerAligns()
    {
        var input = "SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE EXISTS (\n" +
            "           SELECT 1\n" +
            "             FROM u\n" +
            "            WHERE u.id = t.id\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ScalarSubqueryInSelect_InnerAligns()
    {
        var input = "SELECT a, (SELECT MAX(x) FROM u) AS m FROM t";
        var output = Fmt(input);
        var expected =
            "SELECT a, (\n" +
            "           SELECT MAX(x)\n" +
            "             FROM u\n" +
            "       ) AS m\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ScalarSubqueryInWhere_InnerAligns()
    {
        var input = "SELECT * FROM t WHERE a = (SELECT MAX(a) FROM t)";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a = (\n" +
            "           SELECT MAX(a)\n" +
            "             FROM t\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DoublyNestedSubquery_BothInnerScopesAlignLocally()
    {
        // Three QuerySpecs deep: outer / middle / inner. Each has its own local maxKw and
        // its own indent step. Post-4c-step0 (strip-parent-outer on nested pop): innermost
        // SELECT at col 22 = outer body col (7) + middle body col (7) + IndentSize past (4) + 4 from inner's own SELECT pad = 22.
        // See SqlEmitter.EndClauseScope comment.
        var input = "SELECT * FROM t WHERE a IN (SELECT b FROM u WHERE c IN (SELECT d FROM v))";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t\n" +
            " WHERE a IN (\n" +
            "           SELECT b\n" +
            "             FROM u\n" +
            "            WHERE c IN (\n" +
            "                      SELECT d\n" +
            "                        FROM v\n" +
            "                  )\n" +
            "       )\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
