using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting.Visitor;

namespace SqlVersionControl.Tests;

public class PreRepairTests
{
    private const string CorpusDir = "/Users/omer/Documents/Projects/SqlVersionControl/Sorgu";

    [Fact]
    public void SemicolonBeforeWith_RepairsCanonicalCase()
    {
        const string sql = "BEGIN TRANSACTION\nWITH cte AS (SELECT 1 AS x)\nSELECT * FROM cte\n";

        Assert.False(Parses(sql), "premise: original should not parse");
        var repaired = SqlPreRepair.Normalize(sql);
        Assert.True(Parses(repaired), $"repaired must parse:\n{repaired}");
        Assert.Contains(";WITH cte AS", repaired, StringComparison.Ordinal);
    }

    [Fact]
    public void SemicolonBeforeWith_LeavesAlreadyTerminatedAlone()
    {
        const string sql = "BEGIN TRANSACTION;\nWITH cte AS (SELECT 1 AS x)\nSELECT * FROM cte\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void SemicolonBeforeWith_LeavesAlreadyPrefixedAlone()
    {
        const string sql = "BEGIN TRANSACTION\n;WITH cte AS (SELECT 1 AS x)\nSELECT * FROM cte\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void SemicolonBeforeWith_LeavesTopOfBatchAlone()
    {
        const string sql = "WITH cte AS (SELECT 1 AS x)\nSELECT * FROM cte\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void SemicolonBeforeWith_DoesNotMatchTableHint()
    {
        const string sql = "SELECT *\nFROM t\nWITH (NOLOCK)\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void SemicolonBeforeWith_AfterGoIsNotRepaired()
    {
        const string sql = "BEGIN TRANSACTION\nGO\nWITH cte AS (SELECT 1 AS x)\nSELECT * FROM cte\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void SemicolonBeforeWith_HandlesCteWithColumnList()
    {
        const string sql = "BEGIN TRANSACTION\nWITH cte (a, b) AS (SELECT 1, 2)\nSELECT * FROM cte\n";

        Assert.False(Parses(sql), "premise: original should not parse");
        var repaired = SqlPreRepair.Normalize(sql);
        Assert.True(Parses(repaired), $"repaired must parse:\n{repaired}");
    }

    [Fact]
    public void Normalize_PreservesValidCanonicalSqlByteIdentical()
    {
        const string sql =
            "SELECT a, b\n" +
            "FROM dbo.t\n" +
            "WHERE id = 1;\n" +
            "\n" +
            "INSERT INTO dbo.u (a) VALUES (1);\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void Normalize_PreservesCrlfLineEndings()
    {
        const string sql = "SELECT 1\r\nFROM dbo.t\r\nWHERE id = 1;\r\n";

        var repaired = SqlPreRepair.Normalize(sql);
        Assert.Equal(sql, repaired);
    }

    [Fact]
    public void Normalize_EmptyInputUnchanged()
    {
        Assert.Equal(string.Empty, SqlPreRepair.Normalize(string.Empty));
    }

    // Corpus regression: every Sorgu file that parses today must still parse after Normalize.
    // The number that newly parses is informational, not enforced.
    [Fact]
    public void CorpusRegression_NoCollateralDamage()
    {
        if (!Directory.Exists(CorpusDir))
        {
            // Corpus only present in dev workspace; skip silently elsewhere.
            return;
        }

        var files = Directory.EnumerateFiles(CorpusDir, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var brokenAfter = new List<string>();
        int passedBefore = 0, passedAfter = 0;

        foreach (var path in files)
        {
            string sql;
            try { sql = File.ReadAllText(path); }
            catch { continue; }

            bool parsedBefore = Parses(sql);
            if (parsedBefore) passedBefore++;

            var repaired = SqlPreRepair.Normalize(sql);
            bool parsedAfter = Parses(repaired);
            if (parsedAfter) passedAfter++;

            if (parsedBefore && !parsedAfter)
            {
                brokenAfter.Add(Path.GetFileName(path));
            }
        }

        Assert.True(
            brokenAfter.Count == 0,
            $"Pre-repair broke {brokenAfter.Count} previously-parsing files: {string.Join(", ", brokenAfter.Take(10))}");

        // Capture the delta to /tmp for visibility; not asserted.
        File.WriteAllText(
            "/tmp/pre-repair-corpus-delta.txt",
            $"files={files.Count} passedBefore={passedBefore} passedAfter={passedAfter} delta={passedAfter - passedBefore}\n");
    }

    private static bool Parses(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        return errors == null || errors.Count == 0;
    }
}
