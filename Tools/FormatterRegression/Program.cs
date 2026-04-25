using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tools.FormatterRegression;

/// <summary>
/// Regression harness for the formatter revamp. For each .sql file in scripts/formatter-test-corpus/,
/// runs the new formatter, then checks semantic equivalence via canonical round-trip through
/// Sql170ScriptGenerator (parse both → generate both with identical options → string-compare).
///
/// In step 2 this is a passthrough (ScriptDomFormatter delegates to legacy), so every file will
/// round-trip clean. The value arrives at step 3 (Path A spike) and step 4 (visitor work), when
/// real diffs start appearing.
///
/// Usage: dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -- [corpus-path]
/// Default corpus path: scripts/formatter-test-corpus/ relative to the repo root.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var spike = args.Contains("--spike");
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var corpusPath = positional.Length > 0 ? positional[0] : FindDefaultCorpusPath();
        if (!Directory.Exists(corpusPath))
        {
            Console.WriteLine($"Corpus directory not found: {corpusPath}");
            Console.WriteLine("Populate scripts/formatter-test-corpus/ with .sql files (see README.md there).");
            return 1;
        }

        if (spike) return RunSpike(corpusPath);

        // Force the new engine on for the harness regardless of user settings.
        SqlFormatterService.UseNewEngine = true;

        var files = Directory.GetFiles(corpusPath, "*.sql", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Console.WriteLine($"No .sql files found in {corpusPath}. Nothing to check.");
            return 0;
        }

        var reportsDir = Path.Combine(corpusPath, "reports");
        Directory.CreateDirectory(reportsDir);

        int passed = 0, parseFailed = 0, canonicalMismatch = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var original = File.ReadAllText(file);
            var formatted = SqlFormatterService.Format(original);

            if (!TryCanonicalize(original, out var canonOriginal))
            {
                parseFailed++;
                Console.WriteLine($"[parse-fail]  {name} (original does not parse)");
                continue;
            }
            if (!TryCanonicalize(formatted, out var canonFormatted))
            {
                parseFailed++;
                Console.WriteLine($"[parse-fail]  {name} (formatted does not parse — REGRESSION)");
                File.WriteAllText(Path.Combine(reportsDir, $"{name}.formatted"), formatted);
                continue;
            }

            if (canonOriginal == canonFormatted)
            {
                passed++;
                continue;
            }

            canonicalMismatch++;
            Console.WriteLine($"[mismatch]    {name}");
            File.WriteAllText(Path.Combine(reportsDir, $"{name}.formatted"), formatted);
            File.WriteAllText(Path.Combine(reportsDir, $"{name}.canon.orig"), canonOriginal);
            File.WriteAllText(Path.Combine(reportsDir, $"{name}.canon.fmt"), canonFormatted);
        }

        Console.WriteLine();
        Console.WriteLine($"Total:              {files.Length}");
        Console.WriteLine($"Canonical match:    {passed}");
        Console.WriteLine($"Canonical mismatch: {canonicalMismatch}");
        Console.WriteLine($"Parse failures:     {parseFailed}");
        Console.WriteLine($"Reports:            {reportsDir}");

        return canonicalMismatch + parseFailed == 0 ? 0 : 2;
    }

    /// <summary>
    /// Path A spike (sequencing step 3) — for each corpus file write three outputs side-by-side
    /// so the evaluator can read the original, see what Hogimn produces (floor), and see what
    /// Sql170ScriptGenerator produces (Path A candidate) without framing the comparison around
    /// Hogimn. Judge pathA.sql against original.sql.
    /// </summary>
    private static int RunSpike(string corpusPath)
    {
        var files = Directory.GetFiles(corpusPath, "*.sql", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Console.WriteLine($"No .sql files in {corpusPath}. Populate the corpus first.");
            return 1;
        }

        var spikeRoot = Path.Combine(corpusPath, "reports", "spike");
        if (Directory.Exists(spikeRoot)) Directory.Delete(spikeRoot, recursive: true);
        Directory.CreateDirectory(spikeRoot);

        var summary = new System.Text.StringBuilder();
        summary.AppendLine("# Path A Spike — Per-File Output Summary");
        summary.AppendLine();
        summary.AppendLine("Each row compares LOC / parse status of the three outputs written to `<name>/`.");
        summary.AppendLine("Judge `pathA.sql` against `original.sql`. `hogimn.sql` is the current floor, for context only.");
        summary.AppendLine();
        summary.AppendLine("| File | Original LOC | Hogimn LOC | Path A LOC | Parse |");
        summary.AppendLine("|------|--------------|------------|------------|-------|");

        int parseOk = 0, parseFail = 0;
        foreach (var file in files.OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dir = Path.Combine(spikeRoot, name);
            Directory.CreateDirectory(dir);

            var original = File.ReadAllText(file);
            File.WriteAllText(Path.Combine(dir, "original.sql"), original);

            string hogimn;
            try { hogimn = LegacyHogimnFormatter.Format(original); }
            catch (Exception ex) { hogimn = $"-- Hogimn threw: {ex.Message}\n"; }
            File.WriteAllText(Path.Combine(dir, "hogimn.sql"), hogimn);

            var (pathA, parsed) = TryPathA(original);
            File.WriteAllText(Path.Combine(dir, "pathA.sql"), pathA);
            if (parsed) parseOk++; else parseFail++;

            summary.AppendLine($"| {name} | {LineCount(original)} | {LineCount(hogimn)} | {LineCount(pathA)} | {(parsed ? "OK" : "FAIL")} |");
        }

        summary.AppendLine();
        summary.AppendLine($"Totals: parse OK = {parseOk}, parse FAIL = {parseFail} of {files.Length}.");
        File.WriteAllText(Path.Combine(spikeRoot, "SUMMARY.md"), summary.ToString());

        Console.WriteLine(summary.ToString());
        Console.WriteLine($"Wrote per-file outputs under {spikeRoot}");
        return 0;
    }

    private static (string output, bool parsed) TryPathA(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors is { Count: > 0 } || fragment is null)
        {
            var errList = string.Join("\n", (errors ?? new List<ParseError>()).Select(e => $"-- line {e.Line}, col {e.Column}: {e.Message}"));
            return ($"-- Path A parse failed:\n{errList}\n", false);
        }

        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeGroupByClause = true,
            NewLineBeforeHavingClause = true,
            NewLineBeforeOrderByClause = true,
            AlignClauseBodies = true,
            IndentationSize = 4,
        });
        generator.GenerateScript(fragment, out var output);
        return (output, true);
    }

    private static int LineCount(string s) => string.IsNullOrEmpty(s) ? 0 : s.Split('\n').Length;

    private static bool TryCanonicalize(string sql, out string canonical)
    {
        canonical = string.Empty;
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors is { Count: > 0 } || fragment is null) return false;

        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeGroupByClause = true,
            NewLineBeforeHavingClause = true,
            NewLineBeforeOrderByClause = true,
        });
        generator.GenerateScript(fragment, out canonical);
        return true;
    }

    private static string FindDefaultCorpusPath()
    {
        // Walk up from AppContext.BaseDirectory to find the repo root (marker: SqlVersionControl.sln)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SqlVersionControl.sln")))
            dir = dir.Parent;
        return dir == null
            ? "scripts/formatter-test-corpus"
            : Path.Combine(dir.FullName, "scripts", "formatter-test-corpus");
    }
}
