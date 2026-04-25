using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class DmlFormattingTests
{
    private static string Fmt(string input) => ScriptDomFormatter.Format(input, new FormatterOptions());

    private static TSqlScript? ReParse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var result = parser.Parse(reader, out var errors) as TSqlScript;
        return (errors == null || errors.Count == 0) ? result : null;
    }

    // ===== INSERT =====

    [Fact]
    public void Format_InsertValuesShortSingleRow_Inline()
    {
        // D3: INSERT has no clause scope. Target, column list (at +IndentSize), VALUES on own
        // line with the short single row inlined.
        var input = "INSERT INTO dbo.T (a, b, c) VALUES (1, 2, 3)";
        var expected =
            "INSERT INTO dbo.T\n" +
            "    (a, b, c)\n" +
            "VALUES (1, 2, 3)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertValuesMultiRow_WrapsOnePerLine()
    {
        // D5: multi-row VALUES always wraps. Each row on its own line at +IndentSize with
        // trailing comma (D3 CommaStyle).
        var input = "INSERT INTO dbo.T (a, b, c) VALUES (1, 2, 3), (4, 5, 6), (7, 8, 9)";
        var expected =
            "INSERT INTO dbo.T\n" +
            "    (a, b, c)\n" +
            "VALUES\n" +
            "    (1, 2, 3),\n" +
            "    (4, 5, 6),\n" +
            "    (7, 8, 9)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertFromSelect_SourceFollowsTarget()
    {
        // SelectInsertSource dispatches through EmitSubqueryQueryExpression → QuerySpec opens
        // its own clause scope at statement indent (_indentLevel=0), so inner SELECT / FROM /
        // WHERE right-align locally.
        var input = "INSERT INTO dbo.T (a, b) SELECT x, y FROM dbo.Src WHERE z = 1";
        var expected =
            "INSERT INTO dbo.T\n" +
            "    (a, b)\n" +
            "SELECT x, y\n" +
            "  FROM dbo.Src\n" +
            " WHERE z = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertFromUnionSelect_UnionArmsStackInSource()
    {
        // BQE recurses through EmitSubqueryQueryExpression. Both arms' QuerySpecs open their
        // own clause scopes; UNION ALL sits at statement indent between them.
        var input = "INSERT INTO dbo.T (a) SELECT x FROM dbo.A UNION ALL SELECT y FROM dbo.B";
        var expected =
            "INSERT INTO dbo.T\n" +
            "    (a)\n" +
            "SELECT x\n" +
            "  FROM dbo.A\n" +
            "UNION ALL\n" +
            "SELECT y\n" +
            "  FROM dbo.B\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertWithoutColumns_ValuesOnOwnLine()
    {
        // Column list absent → INSERT INTO target on line 1, source on line 2.
        var input = "INSERT INTO dbo.T VALUES (1, 2, 3)";
        var expected =
            "INSERT INTO dbo.T\n" +
            "VALUES (1, 2, 3)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    // ===== UPDATE =====

    [Fact]
    public void Format_UpdateSingleSet_AlignsOuterKeywords()
    {
        // D1: UPDATE / SET / WHERE right-align. maxKw = 6 (UPDATE), body col 7.
        var input = "UPDATE dbo.T SET a = 1 WHERE id = 5";
        var expected =
            "UPDATE dbo.T\n" +
            "   SET a = 1\n" +
            " WHERE id = 5\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateMultiSet_Wraps()
    {
        // Long SET clause wraps at MaxLineLength * 2/3 via EmitWrappedList. Trailing-comma per D3.
        var input = "UPDATE dbo.Employees SET FirstName = @fn, LastName = @ln, Email = @em, Phone = @ph, Address = @ad, City = @ct, Country = @co WHERE EmployeeId = @id";
        var expected =
            "UPDATE dbo.Employees\n" +
            "   SET FirstName = @fn,\n" +
            "       LastName = @ln,\n" +
            "       Email = @em,\n" +
            "       Phone = @ph,\n" +
            "       Address = @ad,\n" +
            "       City = @ct,\n" +
            "       Country = @co\n" +
            " WHERE EmployeeId = @id\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateWithFromAndWhere_AlignsAllClauses()
    {
        // UPDATE...FROM with a JOIN. FROM clause reuses EmitWrappedList + EmitTableReferenceBody
        // (the 4b-iii-a path), so JOINs stack like they do in a plain SELECT's FROM.
        var input = "UPDATE t1 SET t1.status = 'x' FROM dbo.T1 t1 INNER JOIN dbo.T2 t2 ON t1.id = t2.t1_id WHERE t2.flag = 1";
        var expected =
            "UPDATE t1\n" +
            "   SET t1.status = 'x'\n" +
            "  FROM dbo.T1 AS t1\n" +
            "       INNER JOIN dbo.T2 AS t2 ON t1.id = t2.t1_id\n" +
            " WHERE t2.flag = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateWhereSubquery_BreaksInnerToBlock()
    {
        // EmitSearchConditionBody handles subquery-in-WHERE for UPDATE the same way it does for
        // SELECT. Inner SELECT lands at col 11 (outer body col 7 + IndentSize 4).
        var input = "UPDATE dbo.T SET status = 'x' WHERE id IN (SELECT t_id FROM dbo.Aux WHERE flag = 1)";
        var expected =
            "UPDATE dbo.T\n" +
            "   SET status = 'x'\n" +
            " WHERE id IN (\n" +
            "           SELECT t_id\n" +
            "             FROM dbo.Aux\n" +
            "            WHERE flag = 1\n" +
            "       )\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    // ===== DELETE =====

    [Fact]
    public void Format_DeleteFromSimple_SingleClauseKeyword()
    {
        // Simple DELETE form. "DELETE" is the keyword (6 chars); "FROM <target>" is the body.
        // Keeps scope maxKw at 6 so WHERE pads only 1 char (col 1).
        var input = "DELETE FROM dbo.T WHERE id = 5";
        var expected =
            "DELETE FROM dbo.T\n" +
            " WHERE id = 5\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_DeleteExtendedWithJoin_FromClauseStacksJoin()
    {
        // Extended form: DELETE target + FROM <joins>. DELETE and FROM are separate keyword
        // lines in the scope; right-align with WHERE.
        var input = "DELETE t1 FROM dbo.T1 t1 INNER JOIN dbo.T2 t2 ON t1.id = t2.t1_id WHERE t2.flag = 1";
        var expected =
            "DELETE t1\n" +
            "  FROM dbo.T1 AS t1\n" +
            "       INNER JOIN dbo.T2 AS t2 ON t1.id = t2.t1_id\n" +
            " WHERE t2.flag = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_DeleteWhereSubquery_BreaksInnerToBlock()
    {
        var input = "DELETE FROM dbo.T WHERE id IN (SELECT t_id FROM dbo.Aux WHERE flag = 1)";
        var expected =
            "DELETE FROM dbo.T\n" +
            " WHERE id IN (\n" +
            "           SELECT t_id\n" +
            "             FROM dbo.Aux\n" +
            "            WHERE flag = 1\n" +
            "       )\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    // ===== MERGE =====

    [Fact]
    public void Format_MergeMinimal_MatchedUpdate()
    {
        // D4: header scope MERGE INTO / USING / ON (maxKw = "MERGE INTO" = 10). WHEN clause
        // outside scope with action at +IndentSize.
        var input = "MERGE INTO dbo.T AS tgt USING dbo.Src AS src ON tgt.id = src.id WHEN MATCHED THEN UPDATE SET tgt.v = src.v;";
        var expected =
            "MERGE INTO dbo.T AS tgt\n" +
            "     USING dbo.Src AS src\n" +
            "        ON tgt.id = src.id\n" +
            "WHEN MATCHED THEN\n" +
            "    UPDATE SET tgt.v = src.v;\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_MergeAllThreeWhenClauses_Corpus04Shape()
    {
        // Mirrors scripts/formatter-test-corpus/04-merge.sql. The AND in the ON condition
        // right-aligns with MERGE INTO / USING / ON because the ON body is inside the header
        // clause scope (4b-iv BBE in-scope path). $action uppercases to $ACTION via generator —
        // round-trips cleanly.
        var input = "MERGE dbo.EmployeeSalaryHistory AS tgt USING (SELECT e.EmployeeId, e.Salary, GETDATE() AS EffectiveDate FROM dbo.Employees e WHERE e.IsActive = 1) AS src ON tgt.EmployeeId = src.EmployeeId AND tgt.EffectiveDate = src.EffectiveDate WHEN MATCHED AND tgt.Salary <> src.Salary THEN UPDATE SET tgt.Salary = src.Salary, tgt.UpdatedAt = GETDATE() WHEN NOT MATCHED BY TARGET THEN INSERT (EmployeeId, Salary, EffectiveDate, UpdatedAt) VALUES (src.EmployeeId, src.Salary, src.EffectiveDate, GETDATE()) WHEN NOT MATCHED BY SOURCE AND tgt.EffectiveDate > DATEADD(YEAR, -1, GETDATE()) THEN DELETE OUTPUT $action, inserted.EmployeeId, deleted.EmployeeId;";
        var expected =
            "MERGE INTO dbo.EmployeeSalaryHistory AS tgt\n" +
            "     USING (\n" +
            "               SELECT e.EmployeeId, e.Salary, GETDATE() AS EffectiveDate\n" +
            "                 FROM dbo.Employees AS e\n" +
            "                WHERE e.IsActive = 1\n" +
            "           ) AS src\n" +
            "        ON tgt.EmployeeId = src.EmployeeId\n" +
            "       AND tgt.EffectiveDate = src.EffectiveDate\n" +
            "WHEN MATCHED AND tgt.Salary <> src.Salary THEN\n" +
            "    UPDATE SET tgt.Salary = src.Salary, tgt.UpdatedAt = GETDATE()\n" +
            "WHEN NOT MATCHED BY TARGET THEN\n" +
            "    INSERT (EmployeeId, Salary, EffectiveDate, UpdatedAt)\n" +
            "    VALUES (src.EmployeeId, src.Salary, src.EffectiveDate, GETDATE())\n" +
            "WHEN NOT MATCHED BY SOURCE AND tgt.EffectiveDate > DATEADD(YEAR, -1, GETDATE()) THEN\n" +
            "    DELETE\n" +
            "OUTPUT $ACTION, inserted.EmployeeId, deleted.EmployeeId;\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateWithOutput_OutputAlignsInScope()
    {
        // In-scope OUTPUT: OUTPUT (6 chars) shares col 0 with UPDATE (6 chars) after right-align;
        // SET pads 3, WHERE pads 1.
        var input = "UPDATE dbo.T SET a = 1 OUTPUT inserted.a, deleted.a WHERE id = 5";
        var expected =
            "UPDATE dbo.T\n" +
            "   SET a = 1\n" +
            "OUTPUT inserted.a, deleted.a\n" +
            " WHERE id = 5\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    // ===== 4f-iii: TopRowFilter on INSERT / UPDATE / DELETE =====
    // 4f-iii retired the `EmitGeneratorRaw(stmt)` trip-fallbacks at the three TopRowFilter sites.
    // TOP renders inline within the lead clause's body (after INSERT / UPDATE / DELETE keyword,
    // before INTO / target / FROM), so scope maxKw stays at the lead-keyword width — Style-1
    // staircase preserved across SET / FROM / WHERE. Mirrors 4f-ii's QuerySpec TOP-in-SELECT.

    [Fact]
    public void Format_InsertTopN_RendersInline()
    {
        var input = "INSERT TOP (5) INTO t (a) VALUES (1)";
        var expected =
            "INSERT TOP (5) INTO t\n" +
            "    (a)\n" +
            "VALUES (1)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertTopPercent_RendersInline()
    {
        var input = "INSERT TOP (5) PERCENT INTO t (a) VALUES (1)";
        var expected =
            "INSERT TOP (5) PERCENT INTO t\n" +
            "    (a)\n" +
            "VALUES (1)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertTopWithSelectSource_RendersInline()
    {
        var input = "INSERT TOP (5) INTO t (a) SELECT b FROM s";
        var expected =
            "INSERT TOP (5) INTO t\n" +
            "    (a)\n" +
            "SELECT b\n" +
            "  FROM s\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_InsertTopWithOutput_RendersInline()
    {
        var input = "INSERT TOP (5) INTO t (a) OUTPUT inserted.a VALUES (1)";
        var expected =
            "INSERT TOP (5) INTO t\n" +
            "    (a)\n" +
            "OUTPUT inserted.a\n" +
            "VALUES (1)\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateTopN_RendersInline_PreservesScope()
    {
        // UPDATE TOP (5) — TOP rides in body of UPDATE keyword line so scope maxKw stays at
        // UPDATE/SET/WHERE width. SET / WHERE right-align with UPDATE.
        var input = "UPDATE TOP (5) t SET a = 1 WHERE x = 2";
        var expected =
            "UPDATE TOP (5) t\n" +
            "   SET a = 1\n" +
            " WHERE x = 2\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateTopPercent_RendersInline()
    {
        var input = "UPDATE TOP (5) PERCENT t SET a = 1";
        var expected =
            "UPDATE TOP (5) PERCENT t\n" +
            "   SET a = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_UpdateTopWithFromJoin_RendersInline_PreservesScope()
    {
        var input = "UPDATE TOP (5) t SET a = 1 FROM t JOIN s ON t.id = s.id WHERE t.x = 2";
        var expected =
            "UPDATE TOP (5) t\n" +
            "   SET a = 1\n" +
            "  FROM t\n" +
            "       INNER JOIN s ON t.id = s.id\n" +
            " WHERE t.x = 2\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_DeleteTopSimpleForm_RendersInline()
    {
        // Simple form: FromClause is null. TOP rides in body before the body `FROM` keyword.
        var input = "DELETE TOP (5) FROM t WHERE x = 1";
        var expected =
            "DELETE TOP (5) FROM t\n" +
            " WHERE x = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_DeleteTopPercentSimpleForm_RendersInline()
    {
        var input = "DELETE TOP (5) PERCENT FROM t WHERE x = 1";
        var expected =
            "DELETE TOP (5) PERCENT FROM t\n" +
            " WHERE x = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }

    [Fact]
    public void Format_DeleteTopExtendedFormWithJoin_RendersInline_PreservesScope()
    {
        // Extended form: explicit FromClause with JOIN. TOP rides in body of DELETE keyword line.
        // FROM is its own clause keyword line; DELETE / FROM / WHERE all right-align.
        var input = "DELETE TOP (5) t FROM t JOIN s ON t.id = s.id WHERE t.x = 1";
        var expected =
            "DELETE TOP (5) t\n" +
            "  FROM t\n" +
            "       INNER JOIN s ON t.id = s.id\n" +
            " WHERE t.x = 1\n";
        Assert.Equal(expected, Fmt(input));
        Assert.NotNull(ReParse(Fmt(input)));
    }
}
