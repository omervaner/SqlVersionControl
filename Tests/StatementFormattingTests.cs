using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class StatementFormattingTests
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
    public void Format_DeclareVariable_Single_NoInit()
    {
        // 4e-iii: drops the generator-injected `AS` keyword. Source-faithful + corpus-matching.
        // Top-level statements end on a fresh line without trailing `;` (project convention —
        // matches INSERT/UPDATE/SELECT). The terminator is added by EnsureTrailingSemicolon
        // only inside body recursion, exercised in Format_RealProc_DeclareCluster_SpacingAndWrap.
        var input = "DECLARE @x INT;";
        var output = Fmt(input);
        var expected = "DECLARE @x INT\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DeclareVariable_Single_WithInit()
    {
        var input = "DECLARE @x INT = 5;";
        var output = Fmt(input);
        var expected = "DECLARE @x INT = 5\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DeclareVariable_Multi_ShortInline()
    {
        // Three short declarations — total well under 2/3 of MaxLineLength (120 default), inline.
        var input = "DECLARE @x INT = 5, @y NVARCHAR(50) = 'a', @z BIT;";
        var output = Fmt(input);
        var expected = "DECLARE @x INT = 5, @y NVARCHAR (50) = 'a', @z BIT\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DeclareVariable_Multi_LongWraps()
    {
        // Total exceeds 2/3 of MaxLineLength → wrap shape #2: DECLARE alone on its line, each
        // declaration at +IndentSize, comma-trailing. Mirrors the corpus pattern in
        // Sorgu/usp_daily_package_info.sql:12-15 and Sorgu/2161.sql:25-39.
        var input = "DECLARE @customer_name NVARCHAR(200) = 'Acme', @order_date DATETIME2 = GETDATE(), @facility_code VARCHAR(10) = 'WH01', @dry_run BIT = 0;";
        var output = Fmt(input);
        var expected =
            "DECLARE\n" +
            "    @customer_name NVARCHAR (200) = 'Acme',\n" +
            "    @order_date DATETIME2 = GETDATE(),\n" +
            "    @facility_code VARCHAR (10) = 'WH01',\n" +
            "    @dry_run BIT = 0\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_DeclareTableVariable_CleanColumnDdl()
    {
        // 4e-iii: routes through EmitTableDefinitionBody (4d-v helper) so the column DDL
        // renders without the generator's column-alignment padding artifact
        // (`id   INT           ,`). Each column on its own line at +IndentSize.
        var input = "DECLARE @t TABLE (id INT, name NVARCHAR(100));";
        var output = Fmt(input);
        var expected =
            "DECLARE @t TABLE\n" +
            "(\n" +
            "    id INT,\n" +
            "    name NVARCHAR (100)\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_RollbackTransaction_BareKeepsKeyword()
    {
        // 4e-iii: the generator drops TRANSACTION on bare ROLLBACK (`ROLLBACK TRAN;` →
        // `ROLLBACK`). Override re-emits the keyword so the form is symmetric with
        // BEGIN / COMMIT / SAVE TRANSACTION (which the generator handles cleanly).
        var input = "ROLLBACK TRAN;";
        var output = Fmt(input);
        var expected = "ROLLBACK TRANSACTION\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_RollbackTransaction_Named()
    {
        // Savepoint-style ROLLBACK preserves the savepoint name.
        var input = "ROLLBACK TRANSACTION sp1;";
        var output = Fmt(input);
        var expected = "ROLLBACK TRANSACTION sp1\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SelectInto_FallsBackToGeneratorWithoutCrash()
    {
        // Regression — bundled with 4e-iii after Sorgu/2161.sql's `SELECT * INTO #weight FROM
        // (...)` triggered a stack overflow. SelectStatement override's niche-feature trip-flag
        // (Into / On / ComputeClauses / OptimizerHints) used to call EmitFragmentDefault on
        // itself, re-entering the override forever. Now bails to EmitGeneratorRaw early and
        // returns. Locks: re-parse succeeds + SELECT and INTO both survive in output.
        var input = "SELECT * INTO #t FROM dbo.source_table WHERE id > 0";
        var output = Fmt(input);
        Assert.NotNull(ReParse(output));
        Assert.Contains("SELECT", output);
        Assert.Contains("INTO", output);
        Assert.Contains("#t", output);
    }

    [Fact]
    public void Format_RealProc_DeclareCluster_SpacingAndWrap()
    {
        // End-to-end: long DECLARE wrap (4e-iii) + DECLARE/SET cluster spacing (4e-ii-b) +
        // IF/UPDATE block-level breathing room + RETURN cluster. Confirms wrap shape, no `AS`,
        // ROLLBACK keyword preservation, vertical-spacing rule end-to-end inside a real proc.
        var input =
            "CREATE PROCEDURE dbo.usp_x AS BEGIN " +
            "DECLARE @customer_name NVARCHAR(200) = 'Acme', @order_date DATETIME2 = GETDATE(), @facility_code VARCHAR(10) = 'WH01'; " +
            "SET NOCOUNT ON; " +
            "SET @customer_name = 'Updated'; " +
            "IF @@TRANCOUNT > 0 ROLLBACK; " +
            "UPDATE t SET col = @customer_name; " +
            "RETURN 0; " +
            "END";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE\n" +
            "        @customer_name NVARCHAR (200) = 'Acme',\n" +
            "        @order_date DATETIME2 = GETDATE(),\n" +
            "        @facility_code VARCHAR (10) = 'WH01';\n" +
            "    SET NOCOUNT ON;\n" +
            "    SET @customer_name = 'Updated';\n" +
            "\n" +
            "    IF @@TRANCOUNT > 0\n" +
            "        ROLLBACK TRANSACTION;\n" +
            "\n" +
            "    UPDATE t\n" +
            "       SET col = @customer_name;\n" +
            "\n" +
            "    RETURN 0;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SetVariable_ScalarRhs_PassesThrough()
    {
        // 4f: scalar / non-subquery RHS (literals, expressions, += operator) generator-passes
        // through unchanged. The override fires only for ScalarSubquery RHS. Top-level
        // statements end on a fresh line without trailing `;` (project convention).
        var input = "SET @x = 5;";
        var output = Fmt(input);
        Assert.Equal("SET @x = 5\n", output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SetVariable_AddEquals_PassesThrough()
    {
        var input = "SET @x += 1;";
        var output = Fmt(input);
        Assert.Equal("SET @x += 1\n", output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SetVariable_SubqueryRhs_BreakToBlock()
    {
        // 4f: ScalarSubquery RHS reuses the 4b-ii break-to-block pattern — `(` on its own
        // line, body indented, `)` on its own line. The inner SELECT opens its own clause
        // scope with right-aligned keywords (FROM / WHERE pad to the SELECT column).
        var input = "SET @x = (SELECT MAX(id) FROM dbo.t WHERE col = 'a');";
        var output = Fmt(input);
        var expected =
            "SET @x = (\n" +
            "    SELECT MAX(id)\n" +
            "      FROM dbo.t\n" +
            "     WHERE col = 'a'\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_SetVariable_SubqueryRhsTop_Staircase()
    {
        // 4f-ii: niche-feature trip-flag retired. Inner QuerySpec with TOP routes through the
        // visitor's clause scope (not the generator), so SELECT/FROM/WHERE right-align in
        // staircase shape — consistent with every other subquery in the formatter. Replaces
        // the 4f-era shape-light test that locked the generator's left-aligned style.
        var input = "SET @x = (SELECT TOP 1 id FROM dbo.t WHERE col = 'a');";
        var output = Fmt(input);
        var expected =
            "SET @x = (\n" +
            "    SELECT TOP 1 id\n" +
            "      FROM dbo.t\n" +
            "     WHERE col = 'a'\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_ScalarSubquery_WithInnerTop_Staircase()
    {
        // 4f-ii regression: a scalar-subquery-in-SELECT-list whose inner QuerySpec carries TOP
        // now renders staircase. Was generator-style left-aligned before (Style 2 leak).
        var input = "SELECT (SELECT TOP 1 v FROM s WHERE k = t.k) AS v FROM t;";
        var output = Fmt(input);
        var expected =
            "SELECT (\n" +
            "           SELECT TOP 1 v\n" +
            "             FROM s\n" +
            "            WHERE k = t.k\n" +
            "       ) AS v\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
