using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class PivotFormattingTests
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
    public void Format_Pivot_ShortInList_Inline()
    {
        // 4f: PIVOT clause with short IN-list (well under MaxLineLength=120) renders inline
        // on the FROM body line. Aggregate emits without the generator's stray space —
        // `SUM(amt)` not `SUM (amt)` — for parity with the corpus.
        var input = "SELECT * FROM t PIVOT (SUM(amt) FOR cat IN ([a], [b], [c])) AS p;";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t PIVOT (SUM(amt) FOR cat IN ([a], [b], [c])) AS p\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Pivot_LongInList_Wraps()
    {
        // 4f: when assembled inline-PIVOT-clause length > MaxLineLength (120), the IN-list
        // breaks one value per line at +IndentSize from the FROM body column. The closing
        // `))` and `AS alias` land back at the FROM body column.
        var input = "SELECT * FROM t PIVOT (SUM(amt) FOR cat IN ([alpha_value_one], [bravo_value_two], [charlie_value_three], [delta_value_four], [echo_value_five])) AS p;";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t PIVOT (SUM(amt) FOR cat IN (\n" +
            "           [alpha_value_one],\n" +
            "           [bravo_value_two],\n" +
            "           [charlie_value_three],\n" +
            "           [delta_value_four],\n" +
            "           [echo_value_five]\n" +
            "       )) AS p\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Unpivot_ShortInList_Inline()
    {
        // 4f: UNPIVOT mirrors PIVOT — singular ValueColumn and ColumnReferenceExpression
        // InColumns. Same wrap rule; short IN-list stays inline.
        var input = "SELECT * FROM t UNPIVOT (val FOR col IN (a, b, c)) AS u;";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM t UNPIVOT (val FOR col IN (a, b, c)) AS u\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Pivot_OnSubquerySource_CorpusShape()
    {
        // 4f corpus regression: shape from Sorgu/usp_crate_fulfillment_1943616.sql:100 — PIVOT
        // applied to a derived table (subquery in FROM). Source recurses through
        // EmitTableReferenceBody → QueryDerivedTable override (4b-iii-a); PIVOT clause continues
        // on the line where `) AS src` closes.
        var input = "SELECT pick FROM (SELECT 'a' AS pick, 1 AS vol) src PIVOT (SUM(vol) FOR pick IN ([noloc], [loc])) piv;";
        var output = Fmt(input);
        var expected =
            "SELECT pick\n" +
            "  FROM (\n" +
            "           SELECT 'a' AS pick, 1 AS vol\n" +
            "       ) AS src PIVOT (SUM(vol) FOR pick IN ([noloc], [loc])) AS piv\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_Pivot_RoundTripsThroughParser()
    {
        // 4f: idempotence guard. Format then format the output again — both PIVOT shapes
        // (inline and wrapped) must re-parse and produce identical text on the second pass.
        var inputs = new[]
        {
            "SELECT * FROM t PIVOT (SUM(amt) FOR cat IN ([a], [b])) AS p;",
            "SELECT * FROM t PIVOT (SUM(amt) FOR cat IN ([alpha_value_one], [bravo_value_two], [charlie_value_three], [delta_value_four], [echo_value_five])) AS p;",
        };
        foreach (var input in inputs)
        {
            var first = Fmt(input);
            var second = Fmt(first);
            Assert.Equal(first, second);
            Assert.NotNull(ReParse(first));
        }
    }

    [Fact]
    public void Format_OuterApply_FunctionRhs_StacksUnderJoin()
    {
        // 4f regression test (no new override). APPLY-with-function via 4b-iii-a's
        // UnqualifiedJoin path: SchemaObjectFunctionTableReference on the RHS falls through to
        // EmitTableReferenceBody's single-line generator render. Shape from
        // Sorgu/executionhistory.sql.
        var input = "SELECT * FROM sys.dm_exec_query_stats AS deqs CROSS APPLY sys.dm_exec_sql_text(deqs.sql_handle) AS dest WHERE deqs.last_execution_time > '9/29/2021';";
        var output = Fmt(input);
        var expected =
            "SELECT *\n" +
            "  FROM sys.dm_exec_query_stats AS deqs\n" +
            "       CROSS APPLY sys.dm_exec_sql_text(deqs.sql_handle) AS dest\n" +
            " WHERE deqs.last_execution_time > '9/29/2021'\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_NestedParenthesis_Preserved()
    {
        // 4f regression test (no new override). Source-faithful: `((1 + 2) * 3)` round-trips
        // through the generator unchanged. AST parens shape is load-bearing for arithmetic
        // grouping and we don't flatten.
        var input = "SELECT ((1 + 2) * 3) AS x FROM t;";
        var output = Fmt(input);
        var expected =
            "SELECT ((1 + 2) * 3) AS x\n" +
            "  FROM t\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
