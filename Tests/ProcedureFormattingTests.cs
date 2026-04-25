using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class ProcedureFormattingTests
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
    public void Format_CreateProcedure_NoParams_BodySelect()
    {
        var input = "CREATE PROCEDURE dbo.usp_x AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateProcedure_WithParams_ShortInline()
    {
        var input = "CREATE PROCEDURE dbo.usp_x @a INT, @b VARCHAR(10) AS SELECT @a";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "@a INT, @b VARCHAR (10)\n" +
            "AS\n" +
            "SELECT @a;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateProcedure_WithParams_LongWraps()
    {
        var input = "CREATE PROCEDURE dbo.usp_x @customer_name NVARCHAR(200), @order_date DATETIME2, @facility_code VARCHAR(10), @dry_run BIT = 0 AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "@customer_name NVARCHAR (200),\n" +
            "@order_date DATETIME2,\n" +
            "@facility_code VARCHAR (10),\n" +
            "@dry_run BIT=0\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterProcedure()
    {
        var input = "ALTER PROCEDURE dbo.usp_x AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "ALTER PROCEDURE dbo.usp_x\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateOrAlterProcedure()
    {
        var input = "CREATE OR ALTER PROCEDURE dbo.usp_x AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE OR ALTER PROCEDURE dbo.usp_x\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_TestSproc_CaseInCteInsideTransactionInsideTry()
    {
        // Smoke for 4d-i + 4e-i: the test_sproc shape that motivated the slice. Critical
        // assertion: each WHEN of the CASE in the CTE's SELECT list lands on its own line
        // (visitor emission). If this regresses to compact-then-wrap-at-col-70, the slice
        // unwound — the whole point was making the CASE flow through the visitor instead of
        // the generator.
        var input = """
CREATE OR ALTER PROCEDURE dbo.usp_allocate_sorter_lane_picks
@wave_id INT, @facility_code VARCHAR(10), @dry_run BIT = 0
AS
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        WITH eligible_picks AS (
            SELECT p.pick_id, p.order_id, p.priority,
                   CASE WHEN o.order_type = 'RUSH' THEN 1
                        WHEN o.order_type = 'B2B' AND p.priority < 5 THEN 2
                        WHEN o.order_type = 'RETAIL' THEN 3
                        ELSE 9 END AS effective_priority
            FROM dbo.t_pick AS p
                 INNER JOIN dbo.t_order AS o ON o.order_id = p.order_id
            WHERE p.wave_id = @wave_id)
        INSERT INTO dbo.t_pick_lane_assignment (pick_id, lane_id, wave_id)
        SELECT ep.pick_id, 1, @wave_id FROM eligible_picks AS ep;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        RETURN -1;
    END CATCH
    RETURN 0;
END
""";
        var output = Fmt(input);
        File.WriteAllText("/tmp/proc-smoke.txt", output);
        // Critical: each WHEN of the multi-branch CASE on its own line — the visitor's CASE
        // emission, not the generator's compact-then-wrap-at-col-70 (the bug 4d-i + 4e-i
        // unblocked by routing the procedure body through the visitor).
        Assert.Contains("WHEN o.order_type = 'RUSH' THEN 1\n", output);
        Assert.Contains("WHEN o.order_type = 'B2B' AND p.priority < 5 THEN 2\n", output);
        Assert.Contains("WHEN o.order_type = 'RETAIL' THEN 3\n", output);
        // Statement termination: BEGIN TRANSACTION and COMMIT TRANSACTION carry trailing `;`
        // (EnsureTrailingSemicolon in body recursion), so the output re-parses without help.
        Assert.Contains("BEGIN TRANSACTION;\n", output);
        Assert.Contains("COMMIT TRANSACTION;\n", output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_TestSproc_IfNotExistsInsideTry_RightAligns()
    {
        // 4e-ii smoke: a procedure body shape with IF NOT EXISTS (subq) inside BEGIN TRY.
        // Pre-4e-ii, the IF rendered through the generator (left-aligned-keyword style),
        // visually inconsistent with the sibling visitor SELECTs. Critical assertions:
        //   1. Inner subquery's keywords right-align (visitor style).
        //   2. The `IF NOT EXISTS (` opener and the subquery body indent under it.
        //   3. Output re-parses (control-flow `;` invariants intact).
        // Capture-and-lock — if a future change shifts the column math, update the expected
        // and confirm the new shape is still acceptable visitor style.
        var input = """
CREATE OR ALTER PROCEDURE dbo.usp_register_lane @wave_id INT, @lane_id INT
AS
BEGIN
    BEGIN TRY
        IF NOT EXISTS (SELECT 1 FROM dbo.t_lane WHERE lane_id = @lane_id AND active = 1)
        BEGIN
            INSERT INTO dbo.t_lane (lane_id, active) VALUES (@lane_id, 1);
        END
        RETURN 0;
    END TRY
    BEGIN CATCH
        RETURN -1;
    END CATCH
END
""";
        var output = Fmt(input);
        File.WriteAllText("/tmp/proc-smoke-ifnotexists.txt", output);

        // Critical assertion #1: the IF subquery's FROM right-aligns under SELECT (visitor
        // style), not left-aligned with right-padded content (generator style).
        Assert.Contains("    SELECT 1\n", output);
        Assert.Contains("      FROM dbo.t_lane\n", output);
        Assert.Contains("     WHERE lane_id = @lane_id\n", output);
        Assert.Contains("       AND active = 1\n", output);

        // Critical assertion #2: IF NOT EXISTS opener; closing `)` on its own line; then
        // BEGIN at the IF column.
        Assert.Contains("        IF NOT EXISTS (\n", output);
        Assert.Contains("        )\n", output);
        Assert.Contains("        BEGIN\n", output);

        // Critical assertion #3: re-parses cleanly.
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateProcedure_WithEncryption()
    {
        var input = "CREATE PROCEDURE dbo.usp_x WITH ENCRYPTION AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE PROCEDURE dbo.usp_x\n" +
            "WITH ENCRYPTION\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
