using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class BlockStatementTests
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
    public void Format_BeginEnd_OneSelect()
    {
        var input = "BEGIN SELECT 1 END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BeginEnd_MixedOverriddenAndGeneratorFallback()
    {
        // DECLARE / SET fall through to generator (rest of 4e proper). SELECT routes to visitor.
        // Each child gets `;` via EnsureTrailingSemicolon — control-flow statements (DECLARE,
        // SET) need the terminator since the generator omits it for non-DML kinds. 4e-ii-b
        // spacing rule: DECLARE+SET stay tight (both single-liners); SET→SELECT gets a blank
        // line because SELECT is block-level.
        var input = "BEGIN DECLARE @x INT = 0 SET @x = 1 SELECT @x END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    DECLARE @x AS INT = 0;\n" +
            "    SET @x = 1;\n" +
            "\n" +
            "    SELECT @x;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_TryCatch_Basic()
    {
        var input = "BEGIN TRY SELECT 1 END TRY BEGIN CATCH RAISERROR('boom', 16, 1) END CATCH";
        var output = Fmt(input);
        var expected =
            "BEGIN TRY\n" +
            "    SELECT 1;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    RAISERROR ('boom', 16, 1);\n" +
            "END CATCH\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_TryCatch_NestedBeginEndInTryBody()
    {
        // Try-body contains a BEGIN/END block; depth-2 indent inside try, returns to col 0 for END TRY.
        // Inner BEGIN/END is itself a statement, so it gets a trailing `;` after END.
        var input = "BEGIN TRY BEGIN SELECT 1 END END TRY BEGIN CATCH RETURN -1 END CATCH";
        var output = Fmt(input);
        var expected =
            "BEGIN TRY\n" +
            "    BEGIN\n" +
            "        SELECT 1;\n" +
            "    END;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    RETURN -1;\n" +
            "END CATCH\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AtomicBlock_FallsBackToGenerator()
    {
        // BEGIN ATOMIC has Options the visitor doesn't render; explicit fallback keeps content
        // correct rather than silently dropping. Locked as a guard against accidental coverage.
        // The atomic block is a body child of the procedure, so EnsureTrailingSemicolon adds `;`
        // after the block's terminating END.
        var input =
            "CREATE PROCEDURE dbo.usp_x WITH NATIVE_COMPILATION, SCHEMABINDING AS " +
            "BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english') " +
            "SELECT 1 END";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "WITH NATIVE_COMPILATION, SCHEMABINDING\n" +
            "AS\n" +
            "BEGIN ATOMIC\n" +
            "WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')\n" +
            "    SELECT 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_DeclareDeclareTight()
    {
        // 4e-ii-b: two single-liners → no blank line between (both DECLARE).
        var input = "BEGIN DECLARE @a INT = 0 DECLARE @b INT = 1 END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    DECLARE @a AS INT = 0;\n" +
            "    DECLARE @b AS INT = 1;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_DeclareInsertSpaced()
    {
        // 4e-ii-b: single-liner → block-level: blank line (INSERT is block-level).
        var input = "BEGIN DECLARE @a INT = 0 INSERT INTO t (x) VALUES (1) END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    DECLARE @a AS INT = 0;\n" +
            "\n" +
            "    INSERT INTO t\n" +
            "        (x)\n" +
            "    VALUES (1);\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_InsertSetSpaced()
    {
        // 4e-ii-b: the rule's interesting case — SET after a block-level statement gets a
        // blank line, even though SET itself is single-line. "Blank before or after a block,
        // not between consecutive single-liners."
        var input = "BEGIN INSERT INTO t (x) VALUES (1) SET @a = 1 END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    INSERT INTO t\n" +
            "        (x)\n" +
            "    VALUES (1);\n" +
            "\n" +
            "    SET @a = 1;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_TwoBlocksSpaced()
    {
        // Both block-level → blank line between.
        var input = "BEGIN INSERT INTO t (x) VALUES (1) SELECT * FROM t END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    INSERT INTO t\n" +
            "        (x)\n" +
            "    VALUES (1);\n" +
            "\n" +
            "    SELECT *\n" +
            "      FROM t;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_IfReturnSpaced()
    {
        // IF is block-level (multi-line render); RETURN is single-liner. Blank between.
        // The IF body's own ROLLBACK is a child of IF, not a sibling — no spacing rule
        // applies inside the IF.
        var input = "BEGIN IF @@TRANCOUNT > 0 ROLLBACK RETURN -1 END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    IF @@TRANCOUNT > 0\n" +
            "        ROLLBACK;\n" +
            "\n" +
            "    RETURN -1;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_BodySpacing_MixedClusterMatchesScreenshot()
    {
        // Captures the screenshot's CATCH-body shape: IF (block) + DECLARE+DECLARE (two
        // tight single-liners) + INSERT (block) + RAISERROR+RETURN (two tight single-liners).
        var input =
            "BEGIN " +
            "IF @@TRANCOUNT > 0 ROLLBACK " +
            "DECLARE @e1 NVARCHAR(2000) = 'x' " +
            "DECLARE @e2 INT = 1 " +
            "INSERT INTO t (a) VALUES (1) " +
            "RAISERROR ('boom', 16, 1) " +
            "RETURN -1 " +
            "END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    IF @@TRANCOUNT > 0\n" +
            "        ROLLBACK;\n" +
            "\n" +
            "    DECLARE @e1 AS NVARCHAR (2000) = 'x';\n" +
            "    DECLARE @e2 AS INT = 1;\n" +
            "\n" +
            "    INSERT INTO t\n" +
            "        (a)\n" +
            "    VALUES (1);\n" +
            "\n" +
            "    RAISERROR ('boom', 16, 1);\n" +
            "    RETURN -1;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_NestedBeginEnd_DepthTwoIndent()
    {
        // Outer BEGIN at col 0, inner BEGIN at col 4 (outer's body indent), inner SELECT at
        // col 8, inner END returns to col 4 (with `;` since it's a body child), outer END at col 0.
        var input = "BEGIN BEGIN SELECT 1 END END";
        var output = Fmt(input);
        var expected =
            "BEGIN\n" +
            "    BEGIN\n" +
            "        SELECT 1;\n" +
            "    END;\n" +
            "END\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
