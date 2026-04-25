using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class FunctionFormattingTests
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
    public void Format_CreateFunction_Scalar_Minimal()
    {
        var input = "CREATE FUNCTION dbo.fn_x() RETURNS INT AS BEGIN RETURN 1 END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_x()\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_Scalar_ShortParams()
    {
        var input = "CREATE FUNCTION dbo.fn_x(@a INT, @b VARCHAR(10)) RETURNS INT AS BEGIN RETURN @a END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_x\n" +
            "(\n" +
            "    @a INT,\n" +
            "    @b VARCHAR (10)\n" +
            ")\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN @a;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_Scalar_LongParams()
    {
        var input = "CREATE FUNCTION dbo.fn_x(@customer_name NVARCHAR(200), @order_date DATETIME2, @facility_code VARCHAR(10), @dry_run BIT = 0) RETURNS INT AS BEGIN RETURN 1 END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_x\n" +
            "(\n" +
            "    @customer_name NVARCHAR (200),\n" +
            "    @order_date DATETIME2,\n" +
            "    @facility_code VARCHAR (10),\n" +
            "    @dry_run BIT=0\n" +
            ")\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_Scalar_RealisticBody()
    {
        // Exercises body recursion through BeginEndBlockStatement → EmitBodyStatements:
        // DECLARE / IF / SELECT (with right-aligned clause keywords) / RETURN, with the
        // 4e-ii-b vertical-spacing rule putting blank lines around block-level statements
        // (IF, SELECT) and keeping single-liners tight.
        var input = "CREATE FUNCTION dbo.fn_x(@a INT) RETURNS INT AS BEGIN DECLARE @x INT = @a + 1 IF @x > 10 SET @x = 10 SELECT @x = COUNT(*) FROM t_employee WHERE id = @a RETURN @x END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_x\n" +
            "(\n" +
            "    @a INT\n" +
            ")\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE @x AS INT = @a + 1;\n" +
            "\n" +
            "    IF @x > 10\n" +
            "        SET @x = 10;\n" +
            "\n" +
            "    SELECT @x = COUNT(*)\n" +
            "      FROM t_employee\n" +
            "     WHERE id = @a;\n" +
            "\n" +
            "    RETURN @x;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_InlineTvf_Minimal()
    {
        var input = "CREATE FUNCTION dbo.fn_tvf() RETURNS TABLE AS RETURN (SELECT 1 AS x)";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_tvf()\n" +
            "RETURNS TABLE\n" +
            "AS\n" +
            "RETURN (\n" +
            "    SELECT 1 AS x\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_InlineTvf_WithJoinAndWhere()
    {
        // Inline TVF body SELECT routes through SelectStatement override → clause keywords
        // right-align under the indented block; JOINs stack via QualifiedJoin override.
        var input = "CREATE FUNCTION dbo.fn_tvf(@id INT) RETURNS TABLE AS RETURN (SELECT a.x, b.y FROM t1 a JOIN t2 b ON a.id = b.id WHERE a.id = @id)";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_tvf\n" +
            "(\n" +
            "    @id INT\n" +
            ")\n" +
            "RETURNS TABLE\n" +
            "AS\n" +
            "RETURN (\n" +
            "    SELECT a.x, b.y\n" +
            "      FROM t1 AS a\n" +
            "           INNER JOIN t2 AS b ON a.id = b.id\n" +
            "     WHERE a.id = @id\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_MultiStatementTvf()
    {
        // Multi-stmt TVF: column DDL is generator-rendered as a single (multi-line) block for
        // 4d-iii — per-column wrap polish is 4d-v territory. Body BEGIN/END recurses via the
        // existing BeginEndBlockStatement override; INSERT routes through InsertStatement.
        var input = "CREATE FUNCTION dbo.fn_mtvf() RETURNS @t TABLE (col1 INT, col2 VARCHAR(10)) AS BEGIN INSERT INTO @t VALUES (1, 'a'); RETURN END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_mtvf()\n" +
            "RETURNS @t TABLE (\n" +
            "    col1 INT         ,\n" +
            "    col2 VARCHAR (10))\n" +
            "AS\n" +
            "BEGIN\n" +
            "    INSERT INTO @t\n" +
            "    VALUES (1, 'a');\n" +
            "\n" +
            "    RETURN;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterFunction_Scalar()
    {
        var input = "ALTER FUNCTION dbo.fn_x() RETURNS INT AS BEGIN RETURN 1 END";
        var output = Fmt(input);
        var expected =
            "ALTER FUNCTION dbo.fn_x()\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateOrAlterFunction_Scalar()
    {
        var input = "CREATE OR ALTER FUNCTION dbo.fn_x() RETURNS INT AS BEGIN RETURN 1 END";
        var output = Fmt(input);
        var expected =
            "CREATE OR ALTER FUNCTION dbo.fn_x()\n" +
            "RETURNS INT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateFunction_Scalar_WithSchemabinding()
    {
        var input = "CREATE FUNCTION dbo.fn_x() RETURNS INT WITH SCHEMABINDING AS BEGIN RETURN 1 END";
        var output = Fmt(input);
        var expected =
            "CREATE FUNCTION dbo.fn_x()\n" +
            "RETURNS INT\n" +
            "WITH SCHEMABINDING\n" +
            "AS\n" +
            "BEGIN\n" +
            "    RETURN 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
