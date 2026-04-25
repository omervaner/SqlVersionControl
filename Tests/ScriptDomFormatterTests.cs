using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class ScriptDomFormatterTests
{
    private static TSqlScript? ReParse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var result = parser.Parse(reader, out var errors) as TSqlScript;
        return (errors == null || errors.Count == 0) ? result : null;
    }

    [Fact]
    public void Format_SimpleSelect_ProducesParseableOutput()
    {
        var input = "select 1";
        var output = ScriptDomFormatter.Format(input, FormatterOptions.Default);

        Assert.False(string.IsNullOrWhiteSpace(output));
        var reparsed = ReParse(output);
        Assert.NotNull(reparsed);
        Assert.Single(reparsed!.Batches);
    }

    [Fact]
    public void Format_MalformedSql_FallsBackToLegacy()
    {
        var input = "SELECT * FROM";
        var wasUsingNew = SqlFormatterService.UseNewEngine;
        SqlFormatterService.UseNewEngine = true;
        try
        {
            var output = SqlFormatterService.Format(input);
            var legacy = LegacyHogimnFormatter.Format(input);
            Assert.Equal(legacy, output);
        }
        finally
        {
            SqlFormatterService.UseNewEngine = wasUsingNew;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Format_EmptyInput_ReturnsAsIs(string input)
    {
        var output = ScriptDomFormatter.Format(input, FormatterOptions.Default);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Format_MultiStatement_PreservesStatementBoundaries()
    {
        var input = "SELECT 1; SELECT 2;";
        var output = ScriptDomFormatter.Format(input, FormatterOptions.Default);

        Assert.Contains("SELECT 1", output);
        Assert.Contains("SELECT 2", output);

        var reparsed = ReParse(output);
        Assert.NotNull(reparsed);
        Assert.Single(reparsed!.Batches);
        Assert.Equal(2, reparsed.Batches[0].Statements.Count);
    }
}
