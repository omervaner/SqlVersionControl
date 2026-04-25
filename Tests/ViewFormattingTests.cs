using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class ViewFormattingTests
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
    public void Format_CreateView_Minimal()
    {
        var input = "CREATE VIEW dbo.v AS SELECT 1 AS x";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "AS\n" +
            "SELECT 1 AS x\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_WithColumnList()
    {
        var input = "CREATE VIEW dbo.v (a, b) AS SELECT 1, 2";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v (a, b)\n" +
            "AS\n" +
            "SELECT 1, 2\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_WithSchemabinding()
    {
        var input = "CREATE VIEW dbo.v WITH SCHEMABINDING AS SELECT 1 AS x FROM dbo.t";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "WITH SCHEMABINDING\n" +
            "AS\n" +
            "SELECT 1 AS x\n" +
            "  FROM dbo.t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_WithMultipleOptions()
    {
        var input = "CREATE VIEW dbo.v WITH SCHEMABINDING, VIEW_METADATA AS SELECT 1 AS x FROM dbo.t";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "WITH SCHEMABINDING, VIEW_METADATA\n" +
            "AS\n" +
            "SELECT 1 AS x\n" +
            "  FROM dbo.t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterView()
    {
        var input = "ALTER VIEW dbo.v AS SELECT 1 AS x";
        var output = Fmt(input);
        var expected =
            "ALTER VIEW dbo.v\n" +
            "AS\n" +
            "SELECT 1 AS x\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateOrAlterView()
    {
        var input = "CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS x";
        var output = Fmt(input);
        var expected =
            "CREATE OR ALTER VIEW dbo.v\n" +
            "AS\n" +
            "SELECT 1 AS x\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_BodyHasWhere()
    {
        // Body SELECT flows through visitor: SELECT / FROM / WHERE / AND right-align with
        // their own clause scope (maxKw = 6 here, SELECT). Confirms 4d-ii's body routing
        // works — generator-fallback would emit a different shape.
        var input = "CREATE VIEW dbo.v AS SELECT id, name FROM dbo.t WHERE active = 1 AND deleted = 0";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "AS\n" +
            "SELECT id, name\n" +
            "  FROM dbo.t\n" +
            " WHERE active = 1\n" +
            "   AND deleted = 0\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_WithCheckOption()
    {
        // WITH CHECK OPTION trailer lands after the body SELECT, on its own line at col 0
        // (grammar: before terminating `;`, but no `;` is emitted for plain SELECT bodies).
        var input = "CREATE VIEW dbo.v AS SELECT id FROM dbo.t WHERE active = 1 WITH CHECK OPTION";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "AS\n" +
            "SELECT id\n" +
            "  FROM dbo.t\n" +
            " WHERE active = 1\n" +
            "WITH CHECK OPTION\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateView_BodyHasCte()
    {
        // The body's WithCtesAndXmlNamespaces fires through the SelectStatement override —
        // proves the EmitFragmentDefault routing reaches the CTE path.
        var input = "CREATE VIEW dbo.v AS WITH cte AS (SELECT id FROM dbo.t) SELECT * FROM cte";
        var output = Fmt(input);
        var expected =
            "CREATE VIEW dbo.v\n" +
            "AS\n" +
            "WITH cte AS (\n" +
            "    SELECT id\n" +
            "      FROM dbo.t\n" +
            ")\n" +
            "SELECT *\n" +
            "  FROM cte\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateOrAlterView_RealisticBody_RightAlignsThroughVisitor()
    {
        // End-to-end smoke for 4d-ii: CREATE OR ALTER VIEW + WITH SCHEMABINDING + body
        // SELECT with JOIN / WHERE / GROUP BY / HAVING. Proves the body flows through
        // the visitor (right-aligned keywords, joins via the TableReference dispatcher,
        // search condition through EmitSearchConditionBody).
        var input =
            "CREATE OR ALTER VIEW dbo.vw_summary WITH SCHEMABINDING AS " +
            "SELECT o.order_id, COUNT(*) AS line_count " +
            "FROM dbo.t_order AS o " +
            "INNER JOIN dbo.t_order_line AS l ON l.order_id = o.order_id " +
            "WHERE o.active = 1 " +
            "GROUP BY o.order_id " +
            "HAVING COUNT(*) > 1";
        var output = Fmt(input);
        // Body's clause scope has maxKw = 8 (GROUP BY widest), so SELECT/FROM/WHERE/HAVING
        // right-pad to col 8. Confirms the body flows through the visitor — generator output
        // would left-align keywords and right-pad content instead.
        var expected =
            "CREATE OR ALTER VIEW dbo.vw_summary\n" +
            "WITH SCHEMABINDING\n" +
            "AS\n" +
            "  SELECT o.order_id, COUNT(*) AS line_count\n" +
            "    FROM dbo.t_order AS o\n" +
            "         INNER JOIN dbo.t_order_line AS l ON l.order_id = o.order_id\n" +
            "   WHERE o.active = 1\n" +
            "GROUP BY o.order_id\n" +
            "  HAVING COUNT(*) > 1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
