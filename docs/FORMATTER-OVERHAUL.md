# Formatter Revamp

Plan for replacing the SQL Formatter and fixing the SQL Quoter. Two separate changes, written up together because they share the same week of work and the same testing discipline.

---

## Part 1 — SQL Formatter: ScriptDom Migration

### Why the current one is terrible

`Services/SqlFormatterService.cs` wraps `Hogimn.Sql.Formatter`. Hogimn is a **tokenizer**, not a parser. It walks SQL left-to-right and inserts newlines/indent based on keyword tokens. It has no concept of structure.

Consequence: nested SELECTs cannot indent. To the tokenizer, the `SELECT` inside `WHERE x IN (SELECT ...)` is just another SELECT keyword at the root indent level. Parens are punctuation, not scope. Same blindness hits CTEs inside CTEs, CASE WHEN alignment, JOIN wrap behavior, derived tables in FROM, APPLY clauses. All the same bug, different faces.

The regex `StatementStart` splitter compensates for batch-splitting weakness but does nothing for nesting inside a statement — which is the actual pain.

Hogimn's author has publicly said he only maintains the Oracle dialect; T-SQL is best-effort. Not a fixable bug, fundamental limitation.

### Why ScriptDom

`Microsoft.SqlServer.TransactSql.ScriptDom` (NuGet, MIT, cross-platform .NET). Microsoft's own T-SQL parser, open-sourced in 2023. Used by DacFx, sqlpackage, and SSDT. Builds an AST — nesting is free because the tree *is* the nesting.

The only viable alternative is Poor Man's T-SQL Formatter (Tao Klerks), which is AGPL and therefore a non-starter. ApexSQL, Redgate SQL Prompt, dbForge are all closed-source commercial products. There is no permissive-license, AST-based, actively-maintained T-SQL formatter library besides ScriptDom.

### The Decision — Two Paths

**Path A — Minimal:** Use ScriptDom's parser + its built-in `Sql160ScriptGenerator` (or `Sql170ScriptGenerator`). Configure `SqlScriptGeneratorOptions` to taste. Ship.
- Pros: Low effort. A few days. Proven output.
- Cons: Microsoft's generator is deliberately generic and verbose. BEGIN/END each get their own line, nested control flow balloons. Comment preservation is lossy — inline comments can move to weird positions after round-trip. Output is *functional* but not beautiful.

**Path B — Custom Visitor:** Use ScriptDom's parser only. Write our own `TSqlFragmentVisitor` that walks the AST and emits formatted SQL in our taste. Control comment attachment ourselves.
- Pros: Output looks exactly how we want. Comment preservation is ours to own (= ours to get right). Dodges the verbose-generator problem entirely.
- Cons: Real work. Taste decisions have to be made and defended. Long tail of edge cases (OPENJSON, Service Broker, deprecated 2000-era syntax, weird PIVOT) will surface over time regardless of engine choice.

**Going with Path B.** Reasons:
- A previous half-day attempt at a custom formatter failed. Half a day is not a real attempt at this problem. The right shape of investment is a v1 that targets ~95% of real sprocs (common DML, CTEs, CASE, procs, DDL) and accepts that the long tail gets patched over time as it surfaces.
- Path A's comment behavior is a go/no-go gate we can't fully predict until tested. If it fails, we're back to writing a visitor anyway, but now with wasted Path-A effort behind us.
- The formatter *is* taste. Lookout is our tool. Nobody else's taste will match ours.
- Our SQL patterns are consistent (internal WMS sprocs, Solvoyo queries, our own Object Explorer scripts). The 2-5% long tail that bites Redgate across thousands of customers will rarely bite us.

### What Path B Actually Looks Like

Three components:

1. **Parser invocation.** `TSql170Parser(true, SqlEngineType.All)`. On parse errors → fallback to original text unchanged (5 lines). Covers incomplete-syntax-in-editor and unknown-vendor-syntax cases.

2. **Formatter visitor.** Derives from `TSqlFragmentVisitor` (or `TSqlConcreteFragmentVisitor`). Overrides `ExplicitVisit` for each fragment type we care about. Emits to a `StringBuilder` with an indent-level counter.

    Fragment types that need real work (roughly 40-60 total):
    - `SelectStatement`, `QuerySpecification`, `SelectScalarExpression`, `SelectStarExpression`
    - `FromClause`, `NamedTableReference`, `QueryDerivedTable`, `JoinTableReference` (all join variants)
    - `WhereClause`, `BooleanBinaryExpression`, `BooleanParenthesisExpression`
    - `GroupByClause`, `HavingClause`, `OrderByClause`
    - `CommonTableExpression` (CTEs)
    - `CaseExpression`, `SimpleCaseExpression`, `SearchedCaseExpression`
    - `InsertStatement`, `UpdateStatement`, `DeleteStatement`, `MergeStatement`
    - `CreateTableStatement` (columns, constraints, indexes inline, defaults, FKs, checks)
    - `CreateProcedureStatement`, `AlterProcedureStatement`, `CreateOrAlterProcedureStatement`
    - `CreateTriggerStatement`, `CreateViewStatement`, `CreateFunctionStatement`
    - `BeginEndBlockStatement`, `IfStatement`, `WhileStatement`, `TryCatchStatement`
    - `DeclareVariableStatement`, `SetVariableStatement`
    - `PivotedTableReference`, `UnpivotedTableReference`, `CrossApplyTableReference`
    - `ParenthesisExpression`, `ScalarSubquery`

    Everything else: recurse into children with default emission.

3. **Comment attachment.** ScriptDom exposes comments via `ScriptTokenStream` — tokens with `Offset`, `Line`, `Column`, and token type `MultilineComment` / `SingleLineComment`. They are *not* part of the AST. Our visitor needs to know, for each fragment it's about to emit, which tokens in the stream sit between the previous fragment's end offset and this fragment's start offset — those are the "leading comments" for this fragment. Same-line trailing comments (on the same `Line` as the fragment's end) are "trailing comments."

    Four comment cases to handle:
    - Leading block above a statement → emit verbatim, blank line, then the statement
    - Leading same-line before a clause → emit on its own line above
    - Trailing same-line → emit at end of fragment's last line
    - Interior inside an expression → attach to nearest enclosing fragment, emit at appropriate indent

    The attachment rule is deterministic and finite. Four cases, not four hundred. Redgate's investment here is in edge cases (comments inside SELECT lists mid-column, comments between JOIN and ON, etc.) — we'll handle the common cases first and iterate.

### Testing Discipline

This is the load-bearing part. The migration ships behind a settings toggle (`UseNewFormatter`, default off) until it's proven against real code.

**Test corpus:** build from real sources, not synthetic examples.
- All sprocs from REPOSITORY DB
- All sprocs from GRT main DB
- Solvoyo-related queries (known hairy CASE/CTE patterns)
- Our own saved queries folder
- A handful of worst-case weird ones (dynamic SQL wrappers, deep nested CTEs, MERGE with multiple WHEN clauses, CREATE TABLE with 50+ columns and all constraint types)

**Diff harness:**
- Script: read each SQL file → old formatter output → new formatter output → diff
- Manually review every diff for: did nesting improve? did comments survive? did anything break structurally?
- Syntactic equivalence check: parse both outputs with ScriptDom again, compare AST. If ASTs match, the reformat is lossless regardless of whitespace changes.

**Gate to ship:** 95%+ of test corpus formats without losing comments, no AST-level regressions, default options tuned to team taste.

**Rollback plan:** The toggle stays in Settings for a full release cycle after enablement. If reports come in, flip it back to Hogimn while fixing.

### What Stays, What Goes

- `Services/SqlFormatterService.cs` — rewritten, same public API (`Format(string)`). Caller sites unchanged.
- Hogimn NuGet package — stays during transition (behind the toggle), removed after one full release with new formatter enabled by default.
- Regex `StatementStart` splitter — **deleted.** ScriptDom parses `TSqlScript` containing `TSqlBatch` objects; batch splitting is native. This is a simplification, not added complexity.

### Known Limitations We Accept

- **Dynamic SQL contents.** `EXEC sp_executesql @sql` where `@sql` is a string literal won't have its contents formatted. ScriptDom treats it as a string. Future enhancement: detect string-literal-containing-SQL heuristically and recursively format. Not v1.
- **Unknown post-170 syntax.** If SQL Server ships new syntax in 2027 that isn't in our parser version, we fall back to unchanged text. Upgrade the NuGet package when it drops.
- **Half-typed SQL in the editor.** Fallback to unchanged text on parse errors. Better than mangling.

---

## Part 2 — SQL Quoter: Parser Fix

### The bug

`Services/SqlQuoterService.ParseValues` splits on `\n` only. Input like:

```
123 456 789
1011 1213
```

parses as two values: `"123 456 789"` and `"1011 1213"`. Each gets quoted as a single string: `'123 456 789', '1011 1213'`. Wrong.

Whitespace-separated values on the same line are a common paste pattern (from Excel cells, from inline lists in existing queries, from log outputs). The quoter can't handle them.

### Why naive whitespace-split is wrong

Simply splitting on all whitespace breaks legitimate values-with-spaces. `John Smith\nJane Doe` becomes four values instead of two. That's a regression, not a fix.

The usable answer is **per-line heuristic**: split the input on newlines first (newlines always delimit values), then decide per-line how to handle internal separators. Lines with commas split on commas. Lines with whitespace but **no letters** and **no structural markers** (`:` for timestamps, `/` for slash-dates, `.` for IPs / decimals / version numbers) are treated as numeric/ID lists and split on whitespace. Lines with letters, or lines containing `:` / `/` / `.`, are kept as single values (names, phrases, timestamps, IPs). This handles the common pastes — names per line, numbers per line, numbers space-separated on one line, timestamps per line, IPs per line — without guessing on ambiguous mixed content.

### The fix

```csharp
public static List<string> ParseValues(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return [];

    input = input.Trim();

    // Strip ONE matched pair of wrapping parens or brackets (not greedy —
    // avoids eating legitimate trailing parens on values like "abc)")
    if (input.Length >= 2 &&
        ((input[0] == '(' && input[^1] == ')') ||
         (input[0] == '[' && input[^1] == ']')))
    {
        input = input.Substring(1, input.Length - 2).Trim();
    }

    // Split on newlines first — handles \r, \n, and \r\n uniformly
    var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    var result = new List<string>();

    foreach (var rawLine in lines)
    {
        var line = rawLine.Trim();
        if (line.Length == 0) continue;

        string[] parts;
        if (line.Contains(','))
        {
            // Comma wins over whitespace — "1, 2, 3" splits cleanly
            parts = line.Split(',');
        }
        else if (!line.Any(char.IsLetter)
                 && line.Any(char.IsWhiteSpace)
                 && !line.Any(c => c == ':' || c == '/' || c == '.'))
        {
            // Numeric/ID-only line with internal whitespace → split on whitespace.
            // "123 456 789"                 → three values (plain ints).
            // "John Smith"                  → kept whole (has letters).
            // "2026-04-24 10:30:00"         → kept whole (has ':' — timestamp).
            // "192.168.1.1"                 → kept whole (has '.' — IP / version).
            // "2026/04/24 2026/05/01"       → kept whole (has '/' — slash-date).
            parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            // Single value (names with spaces, single token, phrases)
            parts = new[] { line };
        }

        foreach (var p in parts)
        {
            var trimmed = p.Trim().Trim('\'', '"');
            if (trimmed.Length > 0) result.Add(trimmed);
        }
    }

    return result;
}
```

### What this covers

- **Newline-delimited names** — `"John Smith\nJane Doe"` → two values. Names with spaces preserved because the lines contain letters.
- **Whitespace-delimited numbers on one line** — `"123 456 789"` → three values. The original motivating bug. Detected by "line has whitespace, no letters, no structural markers."
- **Structured values kept whole** — ISO timestamps, slash-dates, IPs, decimals, and version numbers have internal `:` / `/` / `.` that mark them as single structural values. Suppressing the whitespace split on those lines prevents `"2026-04-24 10:30:00"` from fracturing into two tokens, and `"192.168.1.1"` from fracturing into four.
- **Mixed across lines** — each line gets independently classified. `"John Smith\n123 456"` → `["John Smith", "123", "456"]`.
- **Comma-delimited** — comma wins over whitespace within a line. `"1, 2, 3"` → three values regardless of surrounding spaces.
- **Parenthesis-wrapped / bracket-wrapped** — one matched pair of outer `()` or `[]` stripped before processing. `((a,b))` only strips one level (safe default to avoid eating legitimate trailing parens).
- **Already-quoted** — `'a', 'b', 'c'` or `"a", "b", "c"` round-trips to `a, b, c`. Makes the quoter idempotent.
- **CRLF line endings** — Windows clipboard pastes work via the `[\r, \n]` split.

The `Trim('\'', '"')` bonus makes the quoter idempotent. Re-quoting already-quoted input no longer produces `'''a'''`.

### What this does NOT cover (and we're fine with)

- **Tab-separated values from Excel where cells contain spaces.** If "Excel paste with spaces in cells" becomes a real workflow, handle `\t` as a first-class delimiter. Not worth adding pre-emptively.
- **Alphanumeric IDs with letters AND spaces on one line.** Something like `"abc123 def456 ghi789"` is kept as one value because the line contains letters. Heuristic choice — better to under-split than wrongly split names. If someone legitimately has IDs with letters separated by spaces, they put them on separate lines.
- **Multiple structured values on one line.** `"192.168.1.1 10.0.0.1"` (two IPs on one line) is kept as one value because of the `.` marker. `"2026-04-24 10:30:00 2026-04-25 11:30:00"` (two timestamps on one line) is kept as one value because of the `:` marker. Same trade-off as alphanumeric IDs — put them on separate lines if you want them separated. The alternative (splitting on whitespace anyway) would fracture the single-timestamp and single-IP cases, which are far more common.
- **Whitespace-separated plain decimals on one line.** `"1.5 2.5 3.5"` is kept as one value because of the `.` marker. If this shows up as a real workflow, put them on separate lines or comma-separate. Rare enough to accept.
- **Nested parens.** `"((a, b))"` strips one layer, leaves `(a` and `b)` as values. Single-layer strip is the safe default; double-wrapping is rare and iterating the strip risks eating legitimate parens in values.

### Tests to add

Unit tests in `Tests/SqlQuoterServiceTests.cs` (or wherever existing quoter tests live, if any):

- `ParseValues_NewlineDelimited_PreservesNamesWithSpaces`
- `ParseValues_WhitespaceDelimited_SplitsOnSpace`
- `ParseValues_CommaDelimited_SplitsAndTrims`
- `ParseValues_ParenWrapped_StripsWrapping`
- `ParseValues_AlreadyQuoted_Idempotent`
- `ParseValues_MixedDelimiters_PerLineClassification`
- `ParseValues_EmptyInput_ReturnsEmptyList`

---

## Sequencing

1. **Quoter fix first.** One file, one method, tests. Ship in a point release. No risk.
2. **Formatter Path B (or A-plus-overrides, pending spike).** Separate branch. Behind `UseNewFormatter` toggle. Path A spike at sequencing step 3 decides whether the full visitor effort is needed. Either way: test corpus comparison before flip. Ship in a minor version bump with the toggle off, enable by default in the following release.

Do not mix these in one PR. The quoter fix is trivial and shippable; the formatter is a project. Keep them separate so the quoter doesn't wait on the formatter and the formatter doesn't ship half-baked to get the quoter out.

---

# Implementation Spec (appended after doc review)

This section translates the strategy above into a concrete spec for CC to implement. Every decision CC would otherwise make on its own should be answered below. If CC hits a question this doesn't answer, stop and ask before guessing.

## Call Site Inventory (verified by reading source)

Three call sites. None need code changes outside the service files themselves — all public API stays backward-compatible.

1. **`Views/QueryEditorHost.Database.cs` → `FormatSqlInEditor()`**
   - Called from `MainWindow.KeyBindings.cs` (Ctrl+Shift+F), the command palette, and the per-tab `FormatSqlRequested` event.
   - Two code paths: if `editor.SelectionLength > 0` formats selected text; else formats entire `editor.Text` and preserves caret by offset.
   - Public signature of `SqlFormatterService.Format(string) : string` must remain unchanged.

2. **`Views/QueryEditorHost.Database.cs` → `QuickQuoteSelection(bool nPrefix)`**
   - Calls `SqlQuoterService.QuickQuote(selected, nPrefix)`.
   - Public signature of `QuickQuote` must remain unchanged.

3. **`Views/SqlQuoterDialog.axaml.cs` → `UpdateOutput()`**
   - Calls `SqlQuoterService.ParseValues(input)` then `SqlQuoterService.FormatValues(values, format)`.
   - Public signatures of both must remain unchanged.

## Settings Toggle Spec

Add to `Services/SettingsService.cs` → `AppSettings` class:

```csharp
// Formatter engine selection (new in v2.16)
public bool UseNewFormatter { get; set; } = false; // default off until validated
```

Reader: `Services/SqlFormatterService.cs` reads this via a static accessor that's set once at startup from `_settings.Settings.UseNewFormatter`. This avoids threading `SettingsService` through every caller.

Settings UI: add a checkbox to `Views/SettingsDialog.axaml` under a new "Advanced" or "Preview Features" section labelled *"Use new SQL formatter (ScriptDom-based, experimental)"*. Tooltip: *"Uses Microsoft's T-SQL parser for structure-aware formatting. Falls back to the legacy formatter on parse errors."*

Debug override: check env var `LOOKOUT_USE_NEW_FORMATTER=1` as well. This lets CC and the test harness toggle without touching user settings. Env var wins if set.

## File Layout

```
Services/
├── SqlFormatterService.cs           # Thin dispatcher, stays as public entry
└── Formatting/                       # New folder
    ├── LegacyHogimnFormatter.cs     # Current implementation, moved here verbatim
    ├── ScriptDomFormatter.cs         # New visitor-based formatter
    ├── FormatterOptions.cs           # Configurable indent, casing, max line length
    └── CommentAttacher.cs            # Comment token → fragment association logic
```

`SqlFormatterService.Format(string)` becomes:

```csharp
public static string Format(string sql)
{
    if (string.IsNullOrWhiteSpace(sql)) return sql;

    if (UseNewEngine)
    {
        try
        {
            return ScriptDomFormatter.Format(sql, FormatterOptions.Default);
        }
        catch
        {
            // Any unexpected exception from the new path → fall back silently
            return LegacyHogimnFormatter.Format(sql);
        }
    }

    return LegacyHogimnFormatter.Format(sql);
}
```

`LegacyHogimnFormatter` is the current `SqlFormatterService` logic moved verbatim — regex splitter, Hogimn calls, all of it. Do not refactor during the move. Verbatim copy only.

## NuGet Changes

Add to `SqlVersionControl.csproj`:

```xml
<PackageReference Include="Microsoft.SqlServer.TransactSql.ScriptDom" Version="170.157.0" />
```

Keep existing `Hogimn.Sql.Formatter` 2.0.2 for the duration of the toggle period. Remove only after one full release with new formatter enabled by default and no reports.

**Pin to exact version 170.157.0, not floating (`170.*`).** The regression harness compares canonical output strings produced by `Sql170ScriptGenerator`. If a NuGet restore six months from now pulls in 170.200.0 with different default formatting, the canonical baseline silently shifts and existing test corpus comparisons become unreliable. Float only when deliberately upgrading ScriptDom as a discrete change, at which point the baseline gets regenerated and diffed.

Earlier ScriptDom versions (<170.157.0) have worse comment preservation, so 170.157.0 is both the version floor and the pin.

Verify the package builds on both `net9.0` and `net10.0` targets (the csproj multi-targets based on SDK version).

## ScriptDom Parser Specifics

Use `TSql170Parser(initialQuotedIdentifiers: true, engineType: SqlEngineType.All)`.

- `initialQuotedIdentifiers: true` matches SSMS/Lookout's expected default for QUOTED_IDENTIFIER.
- `engineType: SqlEngineType.All` accepts syntax from all engine types (boxed SQL Server, Azure SQL DB, Synapse). This is what we want — users paste queries from anywhere.

Verify this constructor overload exists in the pinned ScriptDom version (170.157.0+). Earlier ScriptDom versions only had the `(bool initialQuotedIdentifiers)` constructor without an engine type parameter — if building against an older lockfile, use `TSql170Parser(true)` and accept the default engine type. This is a minor version-pinning check at step 2 of sequencing, not a blocker.

Parse surface: `parser.Parse(new StringReader(sql), out IList<ParseError> errors)` returns a `TSqlFragment`. For full-text format it's a `TSqlScript`. For selection formatting (see below) it might not be.

If `errors.Count > 0` → **do not use the parsed tree**. Fall back to `LegacyHogimnFormatter.Format(sql)` and return. Do not attempt partial formatting. Partial formatting of broken SQL produces worse output than unchanged SQL.

## Selection-Mid-Statement Corner Case

`QueryEditorHost.FormatSqlInEditor()` passes selected text to the formatter. If the user selected a subquery, a CTE body, or half a statement, ScriptDom's `TSqlParser.Parse` expects a full script and will return errors. Legacy Hogimn formats it anyway because it's a tokenizer.

Resolution strategy for selection formatting in `ScriptDomFormatter`:

1. Try parsing as `TSqlScript` (full script). If succeeds → format and return.
2. If fails → try `parser.ParseStatement()`. If succeeds → wrap in a single-batch script, format, return.
3. If fails → try `parser.ParseExpression()`. If succeeds → format as expression.
4. If all fail → fall back to `LegacyHogimnFormatter.Format(sql)` and return. The user pressed the format button; they expect *something* to happen. A silent no-op looks broken. Hogimn's tokenizer-level pass on an unparseable selection is strictly better than unchanged text.

This staircase of parser entrypoints is how SSMS handles the same case.

## Caret Preservation (Existing Bug — Do NOT Fix In This Scope)

`FormatSqlInEditor()` currently preserves caret by saving `editor.CaretOffset` before formatting and restoring it after, guarded by `if (caret <= formatted.Length)`. This is already wrong — the caret lands at a semantically random position because the formatted text has different lengths/offsets.

**Do not attempt to fix this in the formatter revamp.** It is a pre-existing bug, orthogonal to the engine swap. The new formatter should match the existing broken behavior exactly — save offset, restore if within bounds — so there's no regression to report. File a separate issue for the proper fix.

**Note for the follow-up:** Once the AST-based formatter is in place, fixing this properly becomes tractable. Before formatting, find which fragment contains the caret offset by walking the pre-format AST. After formatting, re-emit up to that fragment and use the resulting length as the new caret position. This is a clean 1-2 day job *after* the main rewrite lands, not part of it. The AST unlocks it but scope discipline keeps it out of v1.

## Comment Attachment Rules

ScriptDom exposes comments via `TSqlFragment.ScriptTokenStream` — a flat list of all tokens with `Offset`, `Line`, `Column`, and `TokenType`. Comments appear as `TokenType.MultilineComment` or `TokenType.SingleLineComment`. They are NOT part of the AST.

Four attachment cases:

| Case | Detection | Emit Strategy |
|------|-----------|---------------|
| **Leading block above statement** | Comment token whose end `Line` is ≤ statement's start `Line - 1` | Emit comment verbatim on its own line(s), then blank line, then statement |
| **Leading same-line before clause** | Comment on same `Line` as a clause, positioned before any of the clause's tokens | Emit comment on its own line above the clause, preserving indent |
| **Trailing same-line** | Comment on same `Line` as fragment's last token, positioned after it | Emit at end of fragment's last output line, preceded by 2 spaces |
| **Interior mid-expression** | Comment between tokens of the same fragment | Attach to nearest enclosing list item (column, predicate, etc.); emit before that item on its own line |

Algorithm: walk the token stream once, assign each comment a "target fragment" using the rules above, then during AST emission each visitor checks its token span for assigned comments and emits them.

Known limitation: comments inside parenthesized expressions split across lines are the edge case Redgate has spent years on. For v1, attach to the parent expression's leading position. Revisit if it bites.

## Batch Preservation (GO statements)

Parse produces `TSqlScript` with `.Batches` of `TSqlBatch`. For each batch:

1. Format the batch's statements via the visitor
2. Emit `\nGO\n` between batches (not before first, not after last)

The regex-based `StatementStart` splitter in current `SqlFormatterService.cs` is **deleted entirely**. ScriptDom handles batch boundaries natively. This is a simplification, not new complexity.

Semicolon handling: match existing Hogimn behavior — strip trailing semicolons on each statement before emitting. Configurable via `FormatterOptions.IncludeSemicolons` (default false to match current behavior).

## FormatterOptions — Defaults Deferred Until After Path A Spike

`Services/Formatting/FormatterOptions.cs` holds the configurable formatting choices. The field set below is known and must exist at scaffolding time (step 2). **Default values for the taste-driven fields are intentionally left TBD** until the Path A spike (sequencing step 3) produces real output against the corpus. Deciding leading-vs-trailing commas or line-break thresholds in a vacuum produces worse defaults than deciding them against actual sproc output — the spike's diffs are exactly the moment these answers become obvious.

Field set:

| Field | Type | Purpose | Default |
|-------|------|---------|---------|
| `IndentSize` | `int` | Spaces per indent level | **TBD after spike** |
| `Uppercase` | `bool` | Keywords uppercased | **TBD after spike** |
| `MaxLineLength` | `int` | Threshold where `BooleanBinaryExpression`, column lists, etc. break onto multiple lines | **TBD after spike** |
| `CommaStyle` | enum `Leading` / `Trailing` | `, col` vs `col,` in column lists | **TBD after spike** |
| `AlignAndOrAtStart` | `bool` | `AND` / `OR` at the start of the new line (vs end of previous) — matches the "at start of line, not end" rule in the visitor table | `true` |
| `IncludeSemicolons` | `bool` | Emit trailing semicolons on statements | `false` (matches current Hogimn behavior) |

Scaffolding rule (step 2): create the class with placeholder comments (`// TBD: set after Path A spike`) on the four taste-driven fields and compiler-defaulted values that will visibly fail any test that depends on their output. Do NOT pick arbitrary defaults just to make the class compile — the placeholder comments are the signal to CC and to reviewers that these need the spike's output before they get real values.

The spike (step 3) produces concrete diffs against real sprocs. Looking at those diffs answers all four TBD fields at once. Fill them in as part of step 4a's skeleton PR, not as a separate taste-debate PR.

## Fragment Types — Visitor Override Table

Minimum viable set for v1. Fragments not listed below get default recursion with no special formatting.

| Fragment | Indent Behavior | Notes |
|----------|----------------|-------|
| `TSqlScript` | Root | Iterate batches, emit GO between |
| `TSqlBatch` | Root | Iterate statements, blank line between |
| `SelectStatement` | Current indent | CTE list first (if any), then query expression |
| `QuerySpecification` | +0 | SELECT on own line, FROM on own line, WHERE on own line, etc. |
| `SelectScalarExpression` | +1 | One column per line in list, align commas per FormatterOptions |
| `SelectStarExpression` | +1 | `*` or `alias.*` |
| `FromClause` | +0 | `FROM` keyword, then table references |
| `NamedTableReference` | +1 | `[schema].[table] AS alias` |
| `QueryDerivedTable` | +1 | `(` newline, inner SELECT indented, `) AS alias` |
| `JoinTableReference` (all variants) | +0 | Join keyword on new line at current indent, ON on next line indented +1 |
| `WhereClause` | +0 | `WHERE` keyword, predicates indented +1 |
| `BooleanBinaryExpression` | inherit | `AND`/`OR` at start of line, not end |
| `GroupByClause` / `HavingClause` / `OrderByClause` | +0 | Keyword on own line, items on following lines indented +1 |
| `CommonTableExpression` | +0 | `WITH cte AS (`, body indented +1, `)` at CTE indent |
| `CaseExpression` (both variants) | +1 | `CASE` on own line, `WHEN/THEN/ELSE` aligned, `END` at CASE indent |
| `InsertStatement` / `UpdateStatement` / `DeleteStatement` | Current | Statement keyword + target on one line, SET/VALUES below |
| `MergeStatement` | Current | `MERGE INTO`, `USING`, `ON`, then each `WHEN MATCHED`/`WHEN NOT MATCHED` branch on own line |
| `CreateTableStatement` | Current | Column definitions aligned in a table-column layout, constraints after |
| `CreateProcedureStatement` / `CreateOrAlterProcedureStatement` | Root | Signature line, `AS`, body at +1 |
| `CreateTriggerStatement` / `CreateViewStatement` / `CreateFunctionStatement` | Root | Same pattern as proc |
| `BeginEndBlockStatement` | inherit | `BEGIN` on own line, body +1, `END` at BEGIN indent |
| `IfStatement` | inherit | `IF condition`, statement block indented; `ELSE` on own line, else-block indented |
| `WhileStatement` | inherit | Same as `IfStatement` shape |
| `TryCatchStatement` | inherit | `BEGIN TRY`/`END TRY`/`BEGIN CATCH`/`END CATCH` each on own line |
| `DeclareVariableStatement` | current | One variable per line if multiple declared |
| `SetVariableStatement` | current | `SET @var = expr` one per line |
| `PivotedTableReference` / `UnpivotedTableReference` | +1 | PIVOT/UNPIVOT keyword aligned with derived table |
| `CrossApplyTableReference` / `OuterApplyTableReference` | +0 | Like JOIN |
| `ParenthesisExpression` / `ScalarSubquery` | current | Multiline inner if content needs it, single-line otherwise |

For any fragment not listed, `ExplicitVisit` falls through to the default implementation which recurses into children. This means the formatter degrades gracefully for unusual constructs — they get emitted but without pretty indentation.

## Error Recovery During Visit

If any visitor override throws, catch at the top-level `ScriptDomFormatter.Format` method → fall back to `LegacyHogimnFormatter.Format(sql)`. Do not let exceptions bubble to the editor. Log via `AppLogger.LogError("ScriptDomFormatter", ex)` for diagnostics.

**Exception: during the toggle-off phase (before default flip).** While `UseNewFormatter` defaults to `false`, any user who turns it on is effectively a tester. Silent fallback on exceptions means they never see the bugs, which means we never find them. Until the default flip (sequencing step 6), exceptions in `ScriptDomFormatter` should *also* surface to the user via a visible status message.

### Surfacing Mechanism — Static Event

`SqlFormatterService` is static and has no reference to the active `QueryTabViewModel`. Threading a VM reference through the service contradicts the static-accessor pattern already chosen for the `UseNewFormatter` toggle itself. Use a static event on the service:

```csharp
public static class SqlFormatterService
{
    public static event Action<string>? FallbackOccurred;
    // ...
}
```

Raise it from inside the fallback branch, gated so the message disappears automatically after the default flip:

```csharp
if (UseNewEngine && !DefaultIsNewEngine)
    // user explicitly opted in while default is off → surface the error
    FallbackOccurred?.Invoke("New formatter hit an error — fell back to legacy. Check logs.");
```

Wire the event once at `MainWindow` startup to the active tab's status text, marshalled to the UI thread:

```csharp
SqlFormatterService.FallbackOccurred += msg =>
    Dispatcher.UIThread.Post(() => _queryEditorHost?.ActiveTabViewModel?.SetMessageText(msg));
```

The `Dispatcher.UIThread.Post` guard is free today (the formatter is UI-thread-only) and keeps the wiring correct if the regression harness or a future background workflow raises the event off-thread.

After the default flip (step 6), delete the event raise from `ScriptDomFormatter`. The subscription in `MainWindow` becomes a no-op and can stay or be removed alongside the Hogimn cleanup in step 7.

## Thread Safety and Parse Timeout

Formatter is called synchronously from the UI thread in all current call sites. Keep it synchronous. Do not introduce `async Task<string>` return types. The formatter must complete in under 100ms for typical sprocs (500 lines) and under 250ms worst-case (large CREATE TABLE with 100 columns). If perf is worse than this, there's a visitor bug — don't mask it with async.

**Hard 2-second parse timeout.** Users will occasionally paste enormous inputs (a 50k-line SSMS "Script entire database" dump, a generated migration script, a log capture with embedded SQL). ScriptDom parse on inputs that large can take seconds and block the UI. Wrap the parse on a background thread with a join timeout and return unchanged text if it trips:

```csharp
var task = Task.Run(() => parser.Parse(new StringReader(sql), out _));
if (!task.Wait(TimeSpan.FromSeconds(2))) return sql;
var fragment = task.Result;
```

Return unchanged text on timeout. Do not fall back to Hogimn — if ScriptDom's parser is slow on that input, Hogimn's tokenizer pass over the same input will also be slow. Five lines of code; prevents the UI hang.

## Test Corpus

Build a corpus under `scripts/formatter-test-corpus/` (not committed — gitignored — local only). Sources:

1. **Real sprocs from REPOSITORY DB** — script the top 50 by LOC from the actual production audit log table. These are the worst-case real inputs.
2. **`usp_get_sorter_lane_rpick`** — the WMS sproc recently debugged (v9.1). Known-complex CTE + CASE + dynamic predicate structure.
3. **Solvoyo integration queries** — the `t_solvoyo_load_audit` indexing procs and archiving proc.
4. **AdventureWorks sample** — download Microsoft's sample DB scripts as a sanity control. If these format cleanly and real sprocs don't, the problem is our SQL, not the formatter.
5. **Saved queries folder** — user's `~/Library/Application Support/Lookout/` saved queries.
6. **Edge case handwritten set** — specifically crafted small files:
   - Single-line SELECT (should format identically)
   - SELECT with 15 columns (column alignment)
   - Nested CTE (3 levels deep)
   - CASE with 20 WHEN branches
   - MERGE with all 3 WHEN clauses
   - CREATE TABLE with all constraint types
   - Sproc with TRY/CATCH nested in WHILE nested in IF
   - Dynamic SQL via `sp_executesql` (content should remain untouched as string literal)
   - Query with `--` line comments and `/* */` block comments in every position

## Regression Test Harness

Script at `scripts/formatter-regression.cs` (or a `dotnet test` project if appetite for one). Flow:

```
for each .sql file in corpus:
    original = read file
    formatted = SqlFormatterService.Format(original)  # new engine
    parse1 = ScriptDom.Parse(original)
    parse2 = ScriptDom.Parse(formatted)
    // Canonical round-trip: generate both through Sql170ScriptGenerator with
    // identical options, then string-compare. Equivalent to AST equality for
    // our purposes, much simpler to implement.
    canonical1 = Sql170ScriptGenerator.Generate(parse1)
    canonical2 = Sql170ScriptGenerator.Generate(parse2)
    assert canonical1 == canonical2  # semantic equivalence via canonical form
    write diff to reports/{filename}.diff for manual review
    assert no comments lost: CountComments(original) == CountComments(formatted)
```

Do **not** write a custom `AstEqual` comparer for `TSqlFragment` trees. ScriptDom doesn't ship one, and writing a reflection-walker or per-fragment comparer is a multi-day rabbit hole (positions to ignore, lists to order-compare, nullable children to handle). The canonical round-trip — parse both, generate both through Microsoft's own `Sql170ScriptGenerator` with identical options, string-compare — gives 95% of the safety for 5% of the effort. If both outputs canonicalize to the same string, the reformat preserved semantics.

Ship gate: 95%+ of corpus passes canonical equivalence, comment count is preserved, manual review of diffs finds no regressions vs Hogimn output on critical patterns (nested selects, CTEs, CASE).

## Quoter Test Cases

Unit tests live alongside future `Tests/SqlQuoterServiceTests.cs`. Exact inputs and expected outputs:

| Test | Input | Expected `ParseValues` Output |
|------|-------|-------------------------------|
| Newline-delimited names | `"John Smith\nJane Doe"` | `["John Smith", "Jane Doe"]` |
| Whitespace-delimited numbers (single line) | `"123 456 789"` | `["123", "456", "789"]` |
| Whitespace-delimited numbers (multi-line) | `"123 456 789\n1011 1213"` | `["123", "456", "789", "1011", "1213"]` |
| Comma-delimited | `"1, 2, 3"` | `["1", "2", "3"]` |
| Words with spaces kept whole | `"abc def ghi"` | `["abc def ghi"]` |
| ISO timestamps per line | `"2026-04-24 10:30:00\n2026-04-25 11:30:00"` | `["2026-04-24 10:30:00", "2026-04-25 11:30:00"]` |
| IPs per line | `"192.168.1.1\n10.0.0.1"` | `["192.168.1.1", "10.0.0.1"]` |
| Slash-dates per line | `"2026/04/24\n2026/05/01"` | `["2026/04/24", "2026/05/01"]` |
| Single decimal | `"1.5"` | `["1.5"]` |
| Version numbers per line | `"1.2.3\n1.2.4"` | `["1.2.3", "1.2.4"]` |
| Multiple structured on one line (accepted trade-off) | `"192.168.1.1 10.0.0.1"` | `["192.168.1.1 10.0.0.1"]` |
| Paren-wrapped | `"(1, 2, 3)"` | `["1", "2", "3"]` |
| Bracket-wrapped | `"[1, 2, 3]"` | `["1", "2", "3"]` |
| Paren-wrapped, paren preserved | `"abc)"` | `["abc)"]` |
| Double-wrapped (strip once only) | `"((1, 2, 3))"` | `["(1", "2", "3)"]` |
| Already single-quoted | `"'a', 'b', 'c'"` | `["a", "b", "c"]` |
| Already double-quoted | `"\"a\", \"b\", \"c\""` | `["a", "b", "c"]` |
| Mixed — comma line + number line | `"1, 2\n3 4"` | `["1", "2", "3", "4"]` |
| Mixed — names and numbers | `"John Smith\n123 456"` | `["John Smith", "123", "456"]` |
| CRLF line endings | `"a\r\nb\r\nc"` | `["a", "b", "c"]` |
| Empty | `""` | `[]` |
| Whitespace only | `"   \n  \t  "` | `[]` |
| Single value no delimiter | `"hello"` | `["hello"]` |
| Trailing comma | `"1, 2, 3,"` | `["1", "2", "3"]` |
| Leading/trailing whitespace per value | `"  a  ,  b  "` | `["a", "b"]` |

Classification rule, per line: comma → split on comma; no letters + whitespace + no `:` / `/` / `.` → split on whitespace; otherwise → single value.

## Sequencing — Tightened

0. **Tests/ project scaffold — do this FIRST, before touching any of the interesting work.** The quoter needs unit tests and there's currently no test project at all. This step is the kind of unglamorous infrastructure CC will skip right past on its way to "the real fix" if not forced to do it in isolation. Do not combine with step 1. Do not write the quoter fix without the scaffold landing first.

   Concretely:
   - Create `Tests/SqlVersionControl.Tests.csproj` using xUnit.
   - Multi-target the test project to `net9.0;net10.0` — same shape as the main csproj. CI machines with only .NET 9 run the `net9.0` TFM; dev machines with only .NET 10 (the user's current setup) run the `net10.0` TFM. A single-TFM pin in either direction fails on the other environment — an earlier attempt at `net9.0` only failed locally because no .NET 9 runtime was installed, an attempt at `net10.0` only would break CI. Multi-targeting satisfies both.
   - Add a `ProjectReference` to `../SqlVersionControl.csproj`.
   - The repo currently has no `.sln` at the root. `dotnet test` run from the repo root with no solution file will not auto-discover the test project. **Add `SqlVersionControl.sln` at the repo root** listing both `SqlVersionControl.csproj` and `Tests/SqlVersionControl.Tests.csproj`. One-time fixed cost, unlocks normal IDE workflows and makes `dotnet test` work without arguments.
   - Add a single empty placeholder test (`[Fact] public void ScaffoldIsWired() { }`) in `Tests/Smoke.cs`. Verify the scaffold is green.
   - **Local invocation: `dotnet test -f net10.0`.** A bare `dotnet test` runs both TFMs, and the net9 pass will abort because the user's Mac has only the .NET 10 runtime installed (not the .NET 9 runtime). The `-f` selects the TFM whose runtime is present. On a CI machine with only .NET 9, the invocation would be `dotnet test -f net9.0`. If .NET 9 runtime is later installed locally, bare `dotnet test` will just work.
   - The main `SqlVersionControl.csproj` needed `<Compile Remove="Tests/**" />` (plus Content/EmbeddedResource/None/AvaloniaXaml/AdditionalFiles siblings) to stop it sweeping the Tests sources into the main compile. Same pattern as the existing `lib/**` excludes.

   Prerequisite to step 1. Budget: one hour. Do not cut corners by skipping the solution file and documenting "run `dotnet test Tests/...csproj` explicitly" instead — that just shifts the friction onto every future invocation.

1. **Quoter fix** — one file, one method, tests per the table above. Ship as v2.15.2 point release. No toggle, no risk.

2. **ScriptDom parser plumbing only** — add NuGet, add `AppSettings.UseNewFormatter`, add settings UI checkbox, add debug env var. `ScriptDomFormatter.Format` returns `LegacyHogimnFormatter.Format(sql)` as a placeholder. Merges as a no-op change. Gets ScriptDom into the build and verified across `net9.0` and `net10.0`.

3. **Path A spike — half day.** Before committing to the full custom visitor, run `Sql170ScriptGenerator` against the real corpus (REPOSITORY DB sprocs, `usp_get_sorter_lane_rpick`, a handful of Solvoyo queries). Answer three questions honestly:
   - Is the default output shape tolerable, even if ugly in places?
   - How bad is comment preservation in practice on real code?
   - Can `SqlScriptGenerator` be subclassed to fix our specific objections in ~200 lines of overrides?

   If all three answers lean yes — ship Path A + targeted overrides and skip steps 4–5 entirely. That's weeks of work avoided.

   If any answer is no — proceed to Path B with the information you now have. Half a day spent here is worth it either way: you either shortcut to the answer or you enter the big project with concrete evidence.

4. **Visitor implementation — split by family.** Each sub-step is its own PR, each behind the toggle:
   - 4a. **Skeleton** — `TSqlScript` / `TSqlBatch` / statement-level dispatch. Emits tokens with no indentation initially; proves the visitor wiring works end-to-end.
   - 4b. **DML core** — `SelectStatement`, `QuerySpecification`, `FromClause`, `WhereClause`, joins, `GroupByClause`, `HavingClause`, `OrderByClause`.
   - 4c. **DML extras** — `InsertStatement`, `UpdateStatement`, `DeleteStatement`, `MergeStatement`.
   - 4d. **DDL** — `CreateTableStatement`, `CreateProcedure/View/Function/Trigger`, `AlterProcedure`, `CreateOrAlter*`.
   - 4e. **Control flow** — `BeginEndBlockStatement`, `IfStatement`, `WhileStatement`, `TryCatchStatement`, `DeclareVariableStatement`, `SetVariableStatement`.
   - 4f. **Expressions** — `CaseExpression`, `CommonTableExpression`, `PivotedTableReference`, `CrossApplyTableReference`, `ParenthesisExpression`, `ScalarSubquery`.
   - 4g. **Comment attachment** — the four-case algorithm. Last because it touches all emission points.

5. **Corpus validation** — run regression harness on REPOSITORY DB sprocs + the rest of the corpus. Fix visitor bugs until pass rate ≥95%. Everything below that threshold is a followup issue, not a blocker.

6. **Default flip** — change `UseNewFormatter` default to `true` in `AppSettings`. Drop the surfaced-error message from `ScriptDomFormatter`. Ship. Keep Hogimn package in place.

7. **Hogimn removal** — one release later, if no reports, remove the package reference and `LegacyHogimnFormatter.cs`.

Each numbered step is a separate PR. Do not combine.

**Scope expectations for v1:** The initial release targets ~95% of real sprocs formatting well — common DML, CTEs, CASE, proc bodies, joins, basic DDL. The long tail (OPENJSON quirks, Service Broker, unusual PIVOT shapes, deprecated 2000-era syntax) surfaces over months of real use and gets patched as it comes up. Do not try to cover edge cases pre-emptively — the corpus and user reports will tell you which ones matter.

## Things CC Must NOT Decide On Its Own

If CC reaches any of these, stop and ask:

- Anything involving the caret preservation bug (it stays broken the same way — do not "improve" it).
- Any change to the three public method signatures (`Format`, `ParseValues`, `FormatValues`, `QuickQuote`).
- Any async conversion of the formatter.
- Any "refactor while we're in here" to adjacent files (SettingsService, SqlQuoterDialog, etc.) beyond the additions specified above.
- Any deletion of the Hogimn package or legacy formatter before the sequencing step that calls for it.
- Any addition of new public API surface (new formatting options, new formatter entry points, etc.).
- Any test framework choice other than `dotnet test` + xUnit if tests are being formalized.
- Any change to the XSHD syntax highlighter or the services-layer `SqlSyntaxHighlighter.cs` (those are a separate investigation).

If CC finds a fragment type in real sprocs that isn't in the override table above and formats poorly by default, add it to a followup list — don't expand v1 scope mid-implementation.
