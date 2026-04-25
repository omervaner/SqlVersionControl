# 4a — Visitor Skeleton + Comment Hook Points + TBD Defaults Filled

> **How to use this plan.** This document is the execution spec for sequencing step 4a of the formatter revamp. Read it alongside `docs/FORMATTER-OVERHAUL.md` and `docs/FORMATTER-SPIKE.md`. Those two docs provide the strategic context; this one answers every decision that would otherwise need re-litigation. If something isn't covered here, check the two reference docs first. If still unanswered, stop and ask.

## Purpose

Replace `Services/Formatting/ScriptDomFormatter.cs`'s passthrough stub with a real ScriptDom-based formatter skeleton. This step does **not** produce beautiful output — it produces correct, AST-parsed output via default fragment recursion. The skeleton establishes:

1. The parser invocation + 2-second timeout wrapper.
2. A visitor class derived from `TSqlFragmentVisitor` with entry points for `TSqlScript` and `TSqlBatch`.
3. An internal emitter (`SqlEmitter`) with indent-level state and newline helpers.
4. Comment-attachment hook points called at every emission boundary — all no-op in 4a, filled with the four-case algorithm in 4g.
5. The selection-parse staircase from `FORMATTER-OVERHAUL.md` (`TSqlScript` → `ParseStatement` → `ParseExpression` → legacy fallback).
6. The four `FormatterOptions` defaults that were `TBD` after the Path A spike.
7. `docs/FORMATTER-INTERNALS.md` as the durable home for fragment-handling documentation.

The deliverable is a formatter whose `UseNewEngine=true` path no longer looks like Hogimn (no regex-splitter pathologies) but is otherwise unpolished. Fragment-specific formatting (SELECT, CASE, JOIN, etc.) arrives in 4b–4f; comment attachment arrives in 4g.

## Preconditions (verify before starting)

Run these checks. Stop if any fail.

1. **Step 2 plumbing is in place.** These files exist:
   - `Services/SqlFormatterService.cs` — dispatcher with `UseNewEngine` property, `FallbackOccurred` event, `DefaultIsNewEngine` const.
   - `Services/Formatting/LegacyHogimnFormatter.cs` — Hogimn code verbatim.
   - `Services/Formatting/ScriptDomFormatter.cs` — currently delegates to `LegacyHogimnFormatter.Format`.
   - `Services/Formatting/FormatterOptions.cs` — with four `// TBD: set after Path A spike` fields.
   - `Services/Formatting/CommentAttacher.cs` — placeholder (will be moved in 4a).
   - `Views/SettingsDialog.axaml` — has `UseNewFormatterCheckBox`.
   - `Views/MainWindow.axaml.cs` — sets `SqlFormatterService.UseNewEngine` at startup and subscribes to `FallbackOccurred` with `Dispatcher.UIThread.Post`.
   - `SqlVersionControl.csproj` — pins `Microsoft.SqlServer.TransactSql.ScriptDom` `170.157.0`.

2. **Tests are green.** `dotnet test -f net10.0` → 26/26 passing (1 smoke + 25 quoter).

3. **Harness runs.** `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0` — prints "Nothing to check" or a summary against whatever's in `scripts/formatter-test-corpus/`.

4. **Corpus populated.** At minimum the 11 files used for the Path A spike (6 handwritten + 4 TestDB sprocs + 1 table) under `scripts/formatter-test-corpus/`. If missing, the spike's per-file inputs are documented in FORMATTER-SPIKE.md and the handwritten sources were self-contained — re-create them or skip (they are gitignored).

5. **Local `.NET 10` runtime available** (the user's Mac ships with this; `.NET 9` is not installed). All commands use `-f net10.0`.

---

## Decisions made this session not yet in committed docs

These decisions were reached in conversation and are now authoritative. They supersede anything in `FORMATTER-OVERHAUL.md` that conflicts. Where they do conflict, record the deviation in `FORMATTER-INTERNALS.md` during 4a; do not edit `FORMATTER-OVERHAUL.md`.

### D1 — Path B is the committed path

FORMATTER-SPIKE.md recorded the Path A evaluation and found 100% comment loss (25→0) plus catastrophic CASE/MERGE collapses with `Sql170ScriptGenerator`. The "Path A + overrides" branch of FORMATTER-OVERHAUL.md step 3 is eliminated. All of 4a–4g executes as specified in the doc for Path B. Do **not** reconsider Path A from within 4a.

### D2 — `IncludeSemicolons` flips default from `false` to `true`

FORMATTER-OVERHAUL.md says `IncludeSemicolons = false` to match Hogimn's strip-trailing-semicolons behavior. **Override this.** The new formatter's default is `true`.

Reasons:
- SQL Server increasingly warns or errors on missing semicolons (`THROW` after non-terminated statements, ambiguity in `;WITH` CTEs).
- `Sql170ScriptGenerator` emits semicolons at statement level in the Path A spike outputs even when the option is `false` — the semicolon preservation is close to intrinsic to correct T-SQL.
- Backward-compat with Hogimn is not a goal once the toggle flips. The new formatter is a fresh aesthetic; it should produce correct modern T-SQL.

Change `FormatterOptions.IncludeSemicolons` default to `true`. Document the flip in `FORMATTER-INTERNALS.md` with this rationale.

### D3 — Four TBD `FormatterOptions` defaults — concrete values

These values are derived from the Path A spike's evidence plus the existing `GittyExport/localhost_1433/TestDB/` real sproc patterns. They are the defaults 4a writes into `FormatterOptions`. They can be tuned in 4b–4f based on accumulated corpus output; don't tune them in 4a.

| Field | Value | Rationale |
|---|---|---|
| `IndentSize` | `4` | Matches SSMS/sqlcmd defaults, matches existing `GittyExport` sprocs, matches Hogimn's output. Any other value would be gratuitously different from what the user already reads daily. |
| `Uppercase` | `true` | Every real sproc in `GittyExport` uses uppercase keywords. Matches Hogimn's `Uppercase(true)` config. Is the T-SQL codebase convention here. |
| `MaxLineLength` | `120` | Threshold before `BooleanBinaryExpression`, `SelectScalarExpression` column lists, etc. break multi-line. Hogimn used 80 (`MaxColumnLength(80)`) and the spike showed that 80 causes excessive vertical spreading on real sprocs with long `[schema].[table_name].[column_name]` identifiers. 120 fits typical developer monitors at normal font size and aligns with modern editor conventions (VS Code, Rider). |
| `CommaStyle` | `Trailing` | `GittyExport/localhost_1433/TestDB/StoredProcedures/*.sql` uses trailing commas (`col,\n    col,\n    col`). Path A's leading-comma with keyword-column alignment was explicitly rejected in the spike. Match the existing codebase aesthetic. |

Concrete defaults for the other (already-decided) fields, for completeness:

| Field | Value | Source |
|---|---|---|
| `AlignAndOrAtStart` | `true` | Matches the visitor override table in `FORMATTER-OVERHAUL.md`. |
| `IncludeSemicolons` | `true` | **Flipped from false — see D2.** |

### D4 — Containment directive (structural rule, applies for the rest of the formatter rewrite)

All visitor code lives under `Services/Formatting/Visitor/`. Types there are marked `internal`. Nothing outside `Services/Formatting/` references visitor types directly. The public formatter surface stays exactly `SqlFormatterService.Format(string)` — one method, unchanged signature.

`CLAUDE.md` gets one line about `Services/Formatting/` existing plus a pointer to `FORMATTER-INTERNALS.md`. All fragment-handling documentation goes in `FORMATTER-INTERNALS.md` across 4a–4g. Do not bloat `CLAUDE.md` with visitor details as fragments are implemented.

To let tests in `Tests/` reach internal types, add `[assembly: InternalsVisibleTo("SqlVersionControl.Tests")]` to the main project (via a new `Properties/AssemblyInfo.cs` or top of any existing source file).

### D5 — Parse timeout deferred from step 2, lands in 4a

FORMATTER-OVERHAUL.md's "Thread Safety and Parse Timeout" section specifies a 2-second hard timeout on the parse call, using `Task.Run(...).Wait(TimeSpan.FromSeconds(2))` and returning the original `sql` unchanged on timeout. In step 2, `ScriptDomFormatter.Format` was a passthrough (`return LegacyHogimnFormatter.Format(sql)`) so timing out a no-op would have been dead code. 4a wires the timeout around the real parse.

### D6 — `FORMATTER-OVERHAUL.md` stays frozen during 4a–4g

The doc is the strategic record and should not be edited during implementation. When 4a–4g behavior deviates from it (like D2's semicolon flip), record the deviation in `FORMATTER-INTERNALS.md`, not in FORMATTER-OVERHAUL.md. This keeps `FORMATTER-OVERHAUL.md` as a clean reference for future engineers rather than a changelog.

---

## Scope — what 4a does, concretely

### In-scope

1. Fill in the four `FormatterOptions` defaults per D3.
2. Flip `IncludeSemicolons` default to `true` per D2.
3. Create visitor skeleton under `Services/Formatting/Visitor/` per D4.
4. Wire the 2-second parse timeout per D5.
5. Implement the selection parse staircase (`TSqlScript` → `ParseStatement` → `ParseExpression` → legacy fallback) per `FORMATTER-OVERHAUL.md` § "Selection-Mid-Statement Corner Case".
6. Replace `ScriptDomFormatter.Format` passthrough with real parse + visitor invocation + fallback paths.
7. Create `docs/FORMATTER-INTERNALS.md` with initial sections (D3/D2/D6 summaries, file layout, visitor entry points).
8. Add one line to `CLAUDE.md`.
9. Add `[assembly: InternalsVisibleTo("SqlVersionControl.Tests")]` to the main project.
10. Add `Tests/ScriptDomFormatterTests.cs` with four facts.

### Non-goals for 4a (scope fence)

Do **not** do these — they belong to later sub-steps.

- Fragment-specific formatting (`SelectStatement`, `CaseExpression`, `JoinTableReference`, etc.) — that's 4b–4f.
- Comment attachment logic — 4g; 4a only creates hook points that are no-ops.
- Column alignment, multi-line CASE layout, JOIN layout — 4b–4f.
- Changes to public API surface (`SqlFormatterService.Format(string)` signature stays exactly as-is).
- Edits to `FORMATTER-OVERHAUL.md` (see D6).
- Edits to `SettingsDialog` / `SettingsService` beyond what 4a needs (which is nothing).
- Any change to the caret preservation bug — explicitly scoped out in `FORMATTER-OVERHAUL.md` § "Caret Preservation".
- Benchmarks, perf tests, profiling.
- Any new public formatter entry point.
- Touching `lib/PerformanceStudio/` — submodule, do not modify.

---

## File operations

### Files to create

| Path | Access | Purpose | LOC est |
|---|---|---|---|
| `Services/Formatting/Visitor/TSqlFormatterVisitor.cs` | `internal` | Visitor class derived from `TSqlFragmentVisitor`. Overrides `ExplicitVisit(TSqlScript)` to iterate batches, `ExplicitVisit(TSqlBatch)` to iterate statements. Statements emit via default recursion in 4a. Calls comment-emission hooks at entry/exit of each fragment visited. | ~100 |
| `Services/Formatting/Visitor/SqlEmitter.cs` | `internal` | `StringBuilder` wrapper with indent-level counter, `Write(string)`, `WriteLine(string)`, `WriteKeyword(string)` (respects `FormatterOptions.Uppercase`), `Indent()`/`Dedent()` or `WithIndent()` returning `IDisposable`. | ~120 |
| `Services/Formatting/Visitor/CommentEmission.cs` | `internal` | Static helpers `EmitLeadingCommentsFor(TSqlFragment)` and `EmitTrailingCommentsFor(TSqlFragment)`. No-op in 4a (log-and-return). 4g fills in real logic using CommentAttacher's assignments. | ~40 |
| `Services/Formatting/Visitor/CommentAttacher.cs` | `internal` | Moved from `Services/Formatting/CommentAttacher.cs`. Still a placeholder in 4a; 4g implements the walk-the-token-stream algorithm from `FORMATTER-OVERHAUL.md` § "Comment Attachment Rules". Exposes `Attach(TSqlScript) : Dictionary<TSqlFragment, CommentInfo>` (empty map in 4a). | ~50 |
| `Services/Formatting/Visitor/SelectionParseStaircase.cs` | `internal` | Static `TryParse(string sql, out TSqlFragment fragment)` implementing the three-step staircase: `TSql170Parser.Parse` → `ParseStatement` → `ParseExpression`. Returns the first success. All-fail indicator lets the caller route to legacy. | ~60 |
| `Properties/AssemblyInfo.cs` | N/A | Holds `[assembly: InternalsVisibleTo("SqlVersionControl.Tests")]`. Create only if not already present. | ~3 |
| `Tests/ScriptDomFormatterTests.cs` | `public class` | Four `[Fact]`s — see "Test cases" below. | ~120 |
| `docs/FORMATTER-INTERNALS.md` | doc | See "FORMATTER-INTERNALS.md structure" below. | ~250 |

**New total:** ~745 LOC (code ~530, tests ~120, docs ~250; the doc doesn't count against the code budget).

### Files to modify

| Path | Change | LOC delta |
|---|---|---|
| `Services/Formatting/FormatterOptions.cs` | Replace the four `// TBD: set after Path A spike` markers with D3's concrete values. Flip `IncludeSemicolons` default to `true` per D2. Replace the long rationale XML doc with a one-liner pointing at `FORMATTER-INTERNALS.md`. | ~-5 net (fills in TBDs, shortens doc) |
| `Services/Formatting/ScriptDomFormatter.cs` | Replace the 15-line passthrough stub with the real parse + staircase + visitor invocation + fallback + 2s timeout. Catches visitor exceptions at the top level and falls back to legacy (raising `FallbackOccurred` when the toggle gate allows). | +80 |
| `Services/Formatting/CommentAttacher.cs` | **Delete.** Moved into `Services/Formatting/Visitor/CommentAttacher.cs` per D4. | -30 |
| `CLAUDE.md` | Add one line under `## Project Structure` inside the directory listing or under `## Critical Architecture Patterns`, whichever fits visually. Suggested exact text: `- **Services/Formatting/** — SQL formatter internals (Path B visitor). Public surface is `SqlFormatterService.Format(string)`; internals at [docs/FORMATTER-INTERNALS.md](docs/FORMATTER-INTERNALS.md).` | +1 |

**Modify total delta:** ~+46 LOC net.

### Total footprint

Code: ~530 new + ~46 modified ≈ 576 LOC of compiled code added in 4a.
Tests: ~120 LOC.
Docs: ~250 LOC + 1 line.

This is in line with "skeleton + hooks, not formatting engine." If any single file balloons past 2× the estimate, stop and re-check scope.

---

## Implementation sequence

Each step leaves `dotnet build -f net10.0` green. Do not proceed to the next step if the current one doesn't build.

1. **FormatterOptions values.** Edit `Services/Formatting/FormatterOptions.cs` — fill in D3's four defaults, flip `IncludeSemicolons` per D2. Replace the class-level XML doc comment (currently discussing Path A spike) with a one-line pointer to FORMATTER-INTERNALS.md. Build check.

2. **InternalsVisibleTo.** Create `Properties/AssemblyInfo.cs` (if absent) with `using System.Runtime.CompilerServices;` and `[assembly: InternalsVisibleTo("SqlVersionControl.Tests")]`. Build check.

3. **SqlEmitter.** Create `Services/Formatting/Visitor/SqlEmitter.cs` — internal class, `StringBuilder` field, indent-level int, helpers: `Write(string)`, `WriteLine(string)`, `WriteKeyword(string)` (uppercases per `FormatterOptions.Uppercase`), `NewLine()`, `Indent() : IDisposable` (returns a disposable scope that decrements on dispose), `ToString()`. Build check.

4. **Comment hook placeholders.**
   a. Delete `Services/Formatting/CommentAttacher.cs`.
   b. Create `Services/Formatting/Visitor/CommentAttacher.cs` — internal static class. One method `Attach(TSqlScript script) : IReadOnlyDictionary<TSqlFragment, CommentInfo>` returning an empty dict in 4a. Define `internal record CommentInfo(...)` with fields for leading/trailing/interior comment lists — fields can be empty lists in 4a, used by 4g.
   c. Create `Services/Formatting/Visitor/CommentEmission.cs` — internal static class. Methods `EmitLeadingCommentsFor(SqlEmitter emitter, IReadOnlyDictionary<TSqlFragment, CommentInfo> attachments, TSqlFragment fragment)` and `EmitTrailingCommentsFor(...)` — no-op in 4a (return immediately). Build check.

5. **SelectionParseStaircase.** Create `Services/Formatting/Visitor/SelectionParseStaircase.cs` — internal static class. Method `TryParse(string sql, out TSqlFragment? fragment, out IList<ParseError> errors)` returns true on any success. Tries `TSql170Parser.Parse` for `TSqlScript` first, then `ParseStatement`, then `ParseExpression`. Use `initialQuotedIdentifiers: true`. Build check.

6. **TSqlFormatterVisitor.** Create `Services/Formatting/Visitor/TSqlFormatterVisitor.cs` — internal class derived from `TSqlFragmentVisitor`. Constructor takes `SqlEmitter`, `FormatterOptions`, and the attachments dict from `CommentAttacher.Attach`. Overrides:
   - `ExplicitVisit(TSqlScript script)` — iterate `script.Batches`, emit each batch, emit `\nGO\n` between batches (not before first, not after last).
   - `ExplicitVisit(TSqlBatch batch)` — iterate `batch.Statements`, for each statement call `CommentEmission.EmitLeadingCommentsFor(...)`, then default-recurse (`statement.Accept(this)`), then `EmitTrailingCommentsFor(...)`. Emit blank line between statements.
   
   No other `ExplicitVisit` overrides in 4a — default recursion handles everything else. Build check.

7. **Wire ScriptDomFormatter.** Rewrite `Services/Formatting/ScriptDomFormatter.cs`:
   - `Format(string sql, FormatterOptions options)` becomes real:
     - If `string.IsNullOrWhiteSpace(sql)` → return `sql`.
     - Wrap parse in `Task.Run(() => SelectionParseStaircase.TryParse(sql, out var frag, out var errors))` with `.Wait(TimeSpan.FromSeconds(2))`. If the wait times out, return `sql` unchanged (do not fall back to Hogimn — if ScriptDom times out, Hogimn on the same input will also be slow).
     - If the staircase fails entirely, return `LegacyHogimnFormatter.Format(sql)`.
     - Otherwise, instantiate `SqlEmitter`, run `CommentAttacher.Attach(script)` (or empty dict for non-script fragments), construct `TSqlFormatterVisitor`, call `fragment.Accept(visitor)`, return `emitter.ToString()`.
   - Wrap the whole visitor invocation in `try/catch (Exception ex)` — on any exception, call `AppLogger.LogError("ScriptDomFormatter", ex)` and return `LegacyHogimnFormatter.Format(sql)`. The dispatcher (`SqlFormatterService`) already handles raising `FallbackOccurred` when appropriate, so don't duplicate that here.
   
   Build check.

8. **docs/FORMATTER-INTERNALS.md.** Create the file with the structure in "FORMATTER-INTERNALS.md structure" below. Build not affected.

9. **CLAUDE.md line.** Add the one-line pointer per D4. Keep it one line. No other edits.

10. **Tests.** Create `Tests/ScriptDomFormatterTests.cs` with the four facts in "Test cases" below. `dotnet test -f net10.0` → expect 30/30 green (1 smoke + 25 quoter + 4 new).

11. **Harness sanity run.** `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0 -- --spike` — confirm the spike output still writes (harness itself doesn't crash). Then the default regression mode: `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0` — expect a report with a low-ish canonical-match pass rate. 4a does **not** gate on the pass rate; record whatever the number is in FORMATTER-INTERNALS.md's "Progress log" section for future comparison.

12. **Launch the app.** `dotnet run -f net10.0 --project SqlVersionControl.csproj` in background. Watch for exceptions (TypeLoad, MissingMethod, etc.) for 30s via Monitor. Confirm:
    - App launches clean.
    - Ctrl+Shift+F on a simple SELECT with `UseNewFormatter=true` produces output that is not the Hogimn regex-splitter pathology (no `CREATE\nOR ALTER` split, no `SET\nNOCOUNT ON` split).
    - Ctrl+Shift+F with `UseNewFormatter=false` produces byte-identical Hogimn output.
    - `LOOKOUT_USE_NEW_FORMATTER=1 dotnet run ...` forces the toggle on regardless of settings.
    - Format a query with a deliberate syntax error (e.g. `SELECT * FROM`) with the toggle on — should fall back to legacy Hogimn output, not return unchanged, not throw.

---

## Test cases (`Tests/ScriptDomFormatterTests.cs`)

All four tests are in a single class `ScriptDomFormatterTests`. They reach into internal types via the `InternalsVisibleTo` attribute added in step 2.

1. **`Format_SimpleSelect_ProducesParseableOutput`**
   - Input: `"SELECT 1"` or `"SELECT * FROM dbo.Employees"`
   - Assertion: output is non-empty, differs from input by at most whitespace/casing, and re-parses successfully via `TSql170Parser`.
   - Purpose: proves the full pipeline (parse → visit → emit → parse-again) works end-to-end.

2. **`Format_MalformedSql_FallsBackToLegacy`**
   - Input: `"SELECT * FROM"` (incomplete — parse error)
   - Setup: `SqlFormatterService.UseNewEngine = true`.
   - Assertion: output equals `LegacyHogimnFormatter.Format(input)`. Not the original unchanged; not a thrown exception.
   - Purpose: proves the staircase-all-fail → legacy fallback path.

3. **`Format_EmptyInput_ReturnsAsIs`**
   - Inputs: `""`, `"   "`, `"\n\n"`.
   - Assertion: output equals input.
   - Purpose: guards the early-return branch.

4. **`Format_MultiStatement_PreservesStatementBoundaries`**
   - Input: `"SELECT 1; SELECT 2;"` or a two-statement script with `GO` separator.
   - Assertion: output contains both `SELECT 1` (or the uppercase variant) and `SELECT 2`, re-parses successfully, and the re-parsed `TSqlScript` has the same number of batches/statements as the original.
   - Purpose: proves TSqlScript → TSqlBatch iteration in the visitor.

These tests assume `TSqlFormatterVisitor` and `SqlEmitter` are reachable via the `InternalsVisibleTo` attribute. If a test needs to exercise just the visitor (not the full dispatcher), instantiate them directly. If a test needs to go through the dispatcher, call `SqlFormatterService.Format(sql)` with `UseNewEngine = true` temporarily set.

---

## `docs/FORMATTER-INTERNALS.md` structure

Create with these sections, content sketched:

```
# Formatter Internals

Companion to docs/FORMATTER-OVERHAUL.md. That doc is the strategic record (frozen);
this one is the living implementation record across steps 4a–4g.

## File layout

Services/Formatting/
├── LegacyHogimnFormatter.cs       # Legacy engine — untouched during 4a-4g
├── ScriptDomFormatter.cs          # Parse + dispatch to visitor; timeout + fallback
├── FormatterOptions.cs             # Configurable formatting choices
└── Visitor/
    ├── TSqlFormatterVisitor.cs    # TSqlFragmentVisitor subclass (internal)
    ├── SqlEmitter.cs              # StringBuilder + indent state (internal)
    ├── CommentAttacher.cs         # Token-stream → fragment association (internal)
    ├── CommentEmission.cs         # Emits attached comments at hook points (internal)
    └── SelectionParseStaircase.cs # TSqlScript / ParseStatement / ParseExpression (internal)

All `Visitor/*` types are `internal`. Tests reach them via
[assembly: InternalsVisibleTo("SqlVersionControl.Tests")].

## Public contract

Exactly one entry point: `SqlFormatterService.Format(string) : string`.
No other type in this namespace is part of the public API.

## FormatterOptions defaults — decided values and why

| Field | Value | Source |
|---|---|---|
| IndentSize | 4 | SSMS / GittyExport / Hogimn convention |
| Uppercase | true | GittyExport sprocs; Hogimn config |
| MaxLineLength | 120 | 80 (Hogimn) too tight for real sproc identifier lengths |
| CommaStyle | Trailing | GittyExport aesthetic; Path A leading-comma rejected |
| AlignAndOrAtStart | true | FORMATTER-OVERHAUL visitor override table |
| IncludeSemicolons | true | **Flipped from false**; SQL Server warns on missing; Sql170ScriptGenerator emits them anyway |

## Visitor entry points (as of 4a)

Overridden:
- `ExplicitVisit(TSqlScript)` — iterates batches, emits `GO` between.
- `ExplicitVisit(TSqlBatch)` — iterates statements, calls comment hooks, default-recurses.

Not yet overridden (planned per sub-step):
- 4b: SelectStatement, QuerySpecification, FromClause, WhereClause, joins, GROUP BY, HAVING, ORDER BY, CTE, CASE
- 4c: INSERT / UPDATE / DELETE / MERGE
- 4d: CREATE TABLE / PROCEDURE / VIEW / FUNCTION / TRIGGER / ALTER variants
- 4e: BEGIN/END, IF, WHILE, TRY/CATCH, DECLARE, SET
- 4f: PIVOT / APPLY / ParenthesisExpression / ScalarSubquery
- 4g: Comment attachment rules (populates the dict; emission hooks become real)

Everything not on this list falls through to default recursion — the fragment is emitted by ScriptDom's default behavior. This is intentional graceful degradation for the long tail.

## Parse strategy

Primary: TSql170Parser(initialQuotedIdentifiers: true).
Fallback staircase: TSqlScript → ParseStatement → ParseExpression → LegacyHogimnFormatter.Format.
Hard timeout: 2 seconds via Task.Run + Task.Wait. On timeout: return input unchanged.

## Fragment handling log

(Add an entry per fragment type as 4b–4g implement them. Keep each entry one paragraph: what the fragment is, how we emit it, what taste decisions were made.)

### TSqlScript (4a)
Iterates Batches; emits "\nGO\n" separator between batches (not before first, not after last). No formatting of batch contents beyond default recursion.

### TSqlBatch (4a)
Iterates Statements; calls comment emission hooks at statement boundaries; emits blank line between statements. No per-statement formatting yet.

## Known deviations from FORMATTER-OVERHAUL.md

- **IncludeSemicolons default flipped to true** — FORMATTER-OVERHAUL.md § "Batch Preservation" says false-to-match-Hogimn; spike evidence and SQL Server behavior drove the flip. See D2 in docs/4A-PLAN.md (or this file's FormatterOptions section).

## Progress log

### 4a — (date)
- Visitor skeleton landed. `ExplicitVisit(TSqlScript)` and `ExplicitVisit(TSqlBatch)` only.
- Four TBD FormatterOptions filled.
- 2-second parse timeout wired.
- Regression harness canonical-match pass rate against 11-file corpus: N/N = N%.
  (Low baseline expected; will rise as 4b-4f implement fragment-specific emission.)
```

Keep FORMATTER-INTERNALS.md tight. 4b–4g append to "Fragment handling log" and "Progress log"; they don't rewrite earlier sections.

---

## Exit criteria (all must be green to call 4a done)

### Build

- [ ] `dotnet build -f net10.0 SqlVersionControl.csproj` — no errors, warnings not worse than pre-4a baseline (31 warnings pre-4a).
- [ ] `dotnet build -f net10.0 Tests/SqlVersionControl.Tests.csproj` — no errors.
- [ ] `dotnet build -f net10.0 Tools/FormatterRegression/FormatterRegression.csproj` — no errors.

### Tests

- [ ] `dotnet test -f net10.0` — 30/30 passing (1 smoke + 25 quoter + 4 ScriptDomFormatter). If the count differs because you added more granular tests, verify all new tests pass.
- [ ] All four `ScriptDomFormatterTests` facts pass individually.

### Containment (D4 verification)

- [ ] `grep -rn "public" Services/Formatting/Visitor/` returns no `public class` / `public struct` / `public interface` / `public record` matches. All types are `internal`.
- [ ] `grep -rn "using SqlVersionControl.Services.Formatting.Visitor" --include="*.cs"` returns matches only inside `Services/Formatting/`. No files elsewhere in the repo reference `Visitor/` types.
- [ ] `SqlFormatterService.Format(string)` is the only public entry. `grep -n "public static string Format" Services/SqlFormatterService.cs` should find exactly one method.

### Runtime

- [ ] `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0` executes without the harness itself crashing. A canonical-match pass-rate number is produced; record it in FORMATTER-INTERNALS.md's Progress log.
- [ ] `dotnet run -f net10.0 --project SqlVersionControl.csproj` launches the app without exceptions. Monitor shows no `Exception|Unhandled|TypeLoad|MissingMethod` for 30s.
- [ ] Ctrl+Shift+F in the editor with `UseNewFormatter = true` on a simple SELECT produces parseable output (not Hogimn).
- [ ] Ctrl+Shift+F with `UseNewFormatter = false` produces byte-identical pre-step-2 Hogimn output.
- [ ] `LOOKOUT_USE_NEW_FORMATTER=1` env override forces new formatter regardless of settings.
- [ ] Ctrl+Shift+F on deliberately malformed SQL (e.g. `SELECT * FROM`) with toggle on falls back to legacy output; no exception escapes to the UI.

### Docs

- [ ] `docs/FORMATTER-INTERNALS.md` exists with all the sections sketched above.
- [ ] `CLAUDE.md` has exactly one new line pointing at `Services/Formatting/` and `docs/FORMATTER-INTERNALS.md`.
- [ ] `docs/FORMATTER-OVERHAUL.md` is unchanged.

### State hygiene

- [ ] No new `public` types added in `Services/Formatting/` (only existing public types are `LegacyHogimnFormatter`, `ScriptDomFormatter`, `FormatterOptions`, and they stay as-is in terms of visibility — though `ScriptDomFormatter` body changes significantly).
- [ ] `Services/Formatting/CommentAttacher.cs` no longer exists at the old path — it's under `Visitor/`.

---

## Budget

1–2 days of focused work. If 4a exceeds 3 days something is wrong — stop and re-scope (the skeleton is bigger than the spec suggests, or there's a ScriptDom API surprise, or the containment rule is generating friction). Surface to the user rather than pushing through.

---

## Post-4a

Once all exit criteria are green and the user verifies the launch:

- Record the canonical-match pass rate in FORMATTER-INTERNALS.md's Progress log.
- The next step is 4b (DML core + CTE + CASE per the revised order in `FORMATTER-OVERHAUL.md` § "Visitor implementation — split by family"; note that the plan here treats CTE and CASE as 4b-scoped, matching the user-session reordering recorded in FORMATTER-SPIKE.md's "What this unlocks" section).
- Do **not** start 4b in the same session that completes 4a — separate PR per the sequencing doctrine.
