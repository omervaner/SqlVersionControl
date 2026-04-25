using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class ConditionalStatementTests
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
    public void Format_If_SingleStatementBody()
    {
        // Predicate falls through to inline scaffold; body wrapped in Indent() (single stmt).
        var input = "IF @x > 0 SELECT 1";
        var output = Fmt(input);
        var expected =
            "IF @x > 0\n" +
            "    SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_If_BeginEndBody()
    {
        // BEGIN/END body lands at IF column; inner SELECT at +IndentSize. Trailing `;` after
        // END comes from EmitConditionalBody's EnsureTrailingSemicolon.
        var input = "IF @x > 0 BEGIN SELECT 1 END";
        var output = Fmt(input);
        var expected =
            "IF @x > 0\n" +
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_IfElse_SingleStatements()
    {
        // ELSE on its own line at IF column; both arms wrapped in Indent(). The `;` between
        // THEN body and ELSE doesn't break ScriptDom's parse (ReParse confirms).
        var input = "IF @x > 0 SELECT 1 ELSE SELECT 2";
        var output = Fmt(input);
        var expected =
            "IF @x > 0\n" +
            "    SELECT 1;\n" +
            "ELSE\n" +
            "    SELECT 2;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_IfElse_BothBeginEnd()
    {
        var input = "IF @x > 0 BEGIN SELECT 1 END ELSE BEGIN SELECT 2 END";
        var output = Fmt(input);
        var expected =
            "IF @x > 0\n" +
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END;\n" +
            "ELSE\n" +
            "BEGIN\n" +
            "    SELECT 2;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_If_NotExistsSubquery_RightAligns()
    {
        // The deliverable for 4e-ii: inner subquery's keywords right-align (visitor style),
        // not left-aligned (generator style). BooleanNotExpression decomposes to `NOT ` +
        // recurse, then ExistsPredicate breaks to indented block. Inner QuerySpec opens its
        // own clause scope at _indentLevel=1 → SELECT at col 4, FROM at col 6, WHERE at col 5.
        var input = "IF NOT EXISTS (SELECT 1 FROM t WHERE x = 1) SELECT 2";
        var output = Fmt(input);
        var expected =
            "IF NOT EXISTS (\n" +
            "    SELECT 1\n" +
            "      FROM t\n" +
            "     WHERE x = 1\n" +
            ")\n" +
            "    SELECT 2;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_If_ExistsSubquery_RightAligns()
    {
        // EXISTS without NOT — same break-to-block, predicate dispatches directly.
        var input = "IF EXISTS (SELECT 1 FROM t WHERE x = 1) SELECT 2";
        var output = Fmt(input);
        var expected =
            "IF EXISTS (\n" +
            "    SELECT 1\n" +
            "      FROM t\n" +
            "     WHERE x = 1\n" +
            ")\n" +
            "    SELECT 2;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ElseIf_StairstepLocked()
    {
        // Locks the indent-stairstep produced by natural recursion: ELSE branch's inner
        // IfStatement renders one indent level deeper. Real-world T-SQL uses ELSE IF chains
        // constantly — flag in FORMATTER-INTERNALS.md § Known limitations as a 10-line fix
        // (detect ElseStatement is IfStatement → emit `ELSE IF <cond>` on one line and
        // recurse into nested.ThenStatement / nested.ElseStatement). Out of 4e-ii scope.
        var input = "IF @x = 1 SELECT 1 ELSE IF @x = 2 SELECT 2 ELSE SELECT 3";
        var output = Fmt(input);
        var expected =
            "IF @x = 1\n" +
            "    SELECT 1;\n" +
            "ELSE\n" +
            "    IF @x = 2\n" +
            "        SELECT 2;\n" +
            "    ELSE\n" +
            "        SELECT 3;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_While_SingleStatementBody()
    {
        var input = "WHILE @x > 0 SET @x = @x - 1";
        var output = Fmt(input);
        var expected =
            "WHILE @x > 0\n" +
            "    SET @x = @x - 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_While_BeginEndBody()
    {
        var input = "WHILE @x > 0 BEGIN SET @x = @x - 1 BREAK END";
        var output = Fmt(input);
        var expected =
            "WHILE @x > 0\n" +
            "BEGIN\n" +
            "    SET @x = @x - 1;\n" +
            "    BREAK;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
