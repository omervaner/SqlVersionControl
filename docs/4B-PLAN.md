# 4b — Clause-level Visitor Overrides (split into four slices)

> **How to use this plan.** Execution spec for sequencing step 4b of the formatter revamp. Read it alongside `docs/FORMATTER-OVERHAUL.md` (strategic, frozen) and `docs/FORMATTER-INTERNALS.md` (living implementation record). Those two answer *what* and *why*; this one answers *how, step-by-step*. If something isn't covered, check those first. If still unanswered, stop and ask.
>
> **This plan is split into four slices (4b-i, 4b-ii, 4b-iii, 4b-iv).** Each slice is an independent PR and an independent session. Do not start 4b-ii in the same session that completes 4b-i. The user will reset the context between slices.

## Preconditions (verify before starting 4b-i)

Run these checks. Stop if any fail.

1. **4a is closed.** These exist:
   - `Services/Formatting/Visitor/TSqlFormatterVisitor.cs` with `ExplicitVisit(TSqlScript)` and `ExplicitVisit(TSqlBatch)` overrides, and an `EmitFragmentDefault(TSqlFragment)` method backed by a `Sql170ScriptGenerator` field (carrying a `// Temporary 4a scaffold — removed after 4g …` comment).
   - `Services/Formatting/Visitor/SqlEmitter.cs` with `BeginClauseScope()`, `WriteClauseKeyword(string)`, and nested-scope throws (`InvalidOperationException`).
   - `Services/Formatting/Visitor/{CommentAttacher,CommentEmission,SelectionParseStaircase}.cs`.
   - `Services/Formatting/FormatterOptions.cs` with `AlignClauseBodies = true` default and all four post-TBD defaults filled.
   - `Properties/AssemblyInfo.cs` with `InternalsVisibleTo("SqlVersionControl.Tests")`.
   - `Tests/{ScriptDomFormatterTests,SqlEmitterTests}.cs`.
   - `docs/FORMATTER-INTERNALS.md`.
2. **Tests green.** `dotnet test -f net10.0` → 38/38 passing (1 smoke + 25 quoter + 6 ScriptDomFormatter + 6 SqlEmitter). If the count differs because someone added tests, ensure all pass.
3. **Harness runs.** `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0` prints "Canonical match: 48 / Parse failures: 5" or similar — the 4a baseline.
4. **App launches cleanly.** `dotnet run -f net10.0` on net10.0 boots, no exceptions for 30s.

## Decisions carried over from 4a (authoritative)

These are the 4a-session decisions that bind 4b execution. They are recorded in FORMATTER-INTERNALS.md; summarized here so 4b's fresh context does not re-litigate.

- **D1 — Path B only.** No reconsidering Path A.
- **D2 — `IncludeSemicolons = true`.** Do not flip.
- **D3 — Filled FormatterOptions defaults** (`IndentSize=4`, `Uppercase=true`, `MaxLineLength=120`, `CommaStyle=Trailing`, `AlignAndOrAtStart=true`, `IncludeSemicolons=true`, `AlignClauseBodies=true`). Do not tune in 4b; tune later after the corpus has fragment-specific output.
- **D4 — Containment.** All visitor types `internal` under `Services/Formatting/Visitor/`. Public surface stays `SqlFormatterService.Format(string)`. No new entries in `CLAUDE.md` during 4b; all fragment details land in `FORMATTER-INTERNALS.md`.
- **D5 — 2s parse timeout** is in place (`ScriptDomFormatter.Format`).
- **D6 — `FORMATTER-OVERHAUL.md` is frozen.** Record deviations in `FORMATTER-INTERNALS.md`.
- **D7 (new in 4a)** — **Clause-keyword right-alignment.** SELECT / FROM / WHERE / AND / OR / GROUP BY / ORDER BY / HAVING within a clause scope right-align so the rightmost character of each keyword lands in the same column. Example with `maxKw = 6`:
  ```
  SELECT *
    FROM t_employee
   WHERE 1 = 1
     AND 2 = 2
  ```
  Emitter API: `BeginClauseScope()` + `WriteClauseKeyword(string)`. Buffer-and-flush mechanics fully implemented in 4a.
- **D8 (new in 4a)** — **Generator scaffolding is temporary.** `TSqlFormatterVisitor.EmitFragmentDefault` routing through `Sql170ScriptGenerator` is 4a-only scaffold. Every 4b–4f override replaces one fragment type's fallback. If `EmitFragmentDefault` and the generator field still exist after 4g merges, that is a bug — see the containment rule in `FORMATTER-INTERNALS.md`.
- **D9 (new in 4a)** — **Subqueries always break to multi-line indented block.** Decided during the 4a session after comparing image-3 (inline collapse with continuation at column 15) vs the preferred shape. Target:
  ```
   WHERE id IN (
            SELECT *
              FROM [dbo].[Employees]
         )
  ```
  Inner scope is a fresh `ClauseScope` with local `maxKw`, indented one level from the outer's body column. Implemented in 4b-ii.

---

## Slice scope overview

| Slice | Goal | Touches |
|---|---|---|
| **4b-i** | Outer `QuerySpecification` clauses align. User sees right-aligned SELECT / FROM / WHERE on simple queries in the editor. Nested subqueries still render via generator (not aligned). | Visitor overrides: `SelectStatement`, `QuerySpecification`. Emitter: small `Write` + line-splitting helper. |
| **4b-ii** | Subquery alignment. `ScalarSubquery`, `InPredicate(subquery)`, `ExistsPredicate` break to indented block; inner query gets its own `ClauseScope`. Nested `BeginClauseScope` returns a real disposable instead of throwing. | Visitor overrides: `ScalarSubquery`, `InPredicate`, `ExistsPredicate`. Emitter: stack of clause buffers. |
| **4b-iii** | Joins, column-list multi-line layout, `GROUP BY` / `ORDER BY` element layout, CTEs. | Visitor overrides: `JoinTableReference` family, `SelectScalarExpression` list handling, `GroupByClause` / `OrderByClause` elements, `CommonTableExpression` / `WithCtesAndXmlNamespaces`. |
| **4b-iv** | `CaseExpression` (simple + searched); `BinaryQueryExpression` (UNION / INTERSECT / EXCEPT). | Visitor overrides: `SimpleCaseExpression`, `SearchedCaseExpression`, `BinaryQueryExpression`. |

Each slice's PR should include: code + tests + FORMATTER-INTERNALS.md additions (Fragment handling log, Progress log entry). Each slice leaves the build green and all tests passing. Each slice runs the regression harness and records the new canonical-match pass rate in the Progress log.

---

## 4b-i — QuerySpecification outer-clause alignment

### Purpose

Override `SelectStatement` and `QuerySpecification` so that `SELECT` / `FROM` / `WHERE` / `GROUP BY` / `HAVING` / `ORDER BY` / `OFFSET` clause keywords land inside a `BeginClauseScope` and therefore right-align. Clause *bodies* (the expression tree under each keyword) continue to come from `Sql170ScriptGenerator` in this slice — per-body formatting is 4b-iii's job. Subqueries in the body continue to render via the generator (inline, not aligned) — breaking-to-block is 4b-ii's job.

The **deliverable**: the user's screenshot query

```sql
SELECT * FROM [dbo].[Employees] WHERE id IN (SELECT * FROM [dbo].[Employees])
```

renders as (something structurally equivalent to):

```
SELECT *
  FROM [dbo].[Employees]
 WHERE id IN (SELECT *
                FROM    [dbo].[Employees])
```

Outer `SELECT` / `FROM` / `WHERE` right-aligned (our alignment). Inner subquery inside the `WHERE` body is whatever the generator produces — imperfect, acknowledged, 4b-ii addresses it.

### Scope — in

1. Override `ExplicitVisit(SelectStatement)` to:
   - Emit `WithCtesAndXmlNamespaces` (if present) via generator scaffold (4b-iii handles CTEs; 4b-i just falls back).
   - Dispatch `QueryExpression` (`statement.QueryExpression.Accept(this)`). If it's `QuerySpecification`, our override runs; if it's `BinaryQueryExpression`, falls back to `EmitFragmentDefault` (4b-iv).
   - Emit top-level `OrderByClause` (separate from QuerySpec's own ORDER BY — this is the one on `SelectStatement`) — for 4b-i, extract clause text via generator and emit as aligned keyword + body.
   - Emit `ForClause`, `OptionalXmlNamespacesClause`, `Into`, `OptimizerHints` via fallback — defer.
2. Override `ExplicitVisit(QuerySpecification)` to open a `ClauseScope` and emit each present clause as `WriteClauseKeyword(kw); WriteClauseBody(body)`:
   - `SELECT` (includes optional `TOP`, `DISTINCT`)
   - `FROM` (body from generator in 4b-i — joins come in 4b-iii)
   - `WHERE`
   - `GROUP BY`
   - `HAVING`
   - `ORDER BY` (the one inside QuerySpec, if present)
   - `OFFSET` / `FETCH` via generator fallback (offset clause handling is niche; defer)
3. Emitter helper for multi-line clause bodies from generator output: small method that splits on `\n`, strips leading whitespace per line, writes as `Write + NewLine` sequence so the continuation-line handling in the scope buffer applies correctly.
4. Tests in `Tests/QuerySpecificationFormattingTests.cs`:
   - `SELECT *` → `SELECT *\n`
   - `SELECT * FROM t` → aligned two-line
   - `SELECT * FROM t WHERE x = 1` → aligned three-line
   - `SELECT a, b FROM t WHERE x = 1` → outer aligned; multi-column body is whatever the generator renders (single-line for short cases)
   - `SELECT * FROM t WHERE x = 1 GROUP BY y HAVING COUNT(*) > 1 ORDER BY z` → six-clause aligned statement
   - The screenshot query: asserts the outer aligns; asserts the output re-parses; does **not** assert a specific inner-subquery shape (that's 4b-ii).

### Scope — explicit non-goals for 4b-i

- Nested `BeginClauseScope` — still throws. 4b-ii.
- `ScalarSubquery` / `InPredicate(subquery)` / `ExistsPredicate` — still fall through to generator. 4b-ii.
- `JoinTableReference` — falls through to generator. 4b-iii.
- Multi-line column list layout — falls through. 4b-iii.
- CTE formatting — falls through. 4b-iii.
- CASE formatting — falls through. 4b-iv.
- UNION / INTERSECT / EXCEPT — falls through. 4b-iv.

### File operations

**Create:**
- `Services/Formatting/Visitor/ClauseBodyEmitter.cs` — static helper class with `WriteClauseBody(SqlEmitter emitter, SqlScriptGenerator generator, TSqlFragment body)`. Internal. ~30 LOC.
- `Tests/QuerySpecificationFormattingTests.cs` — ~150 LOC, six or seven `[Fact]`s.

**Modify:**
- `Services/Formatting/Visitor/TSqlFormatterVisitor.cs` — add `ExplicitVisit(SelectStatement)` and `ExplicitVisit(QuerySpecification)`. The two new overrides together: ~80 LOC.
- `Services/Formatting/Visitor/SqlEmitter.cs` — no additions needed. Existing continuation-line behavior covers the multi-line body case if the visitor splits generator output into `Write + NewLine` calls. Verify by test and only touch if a test proves a gap.
- `docs/FORMATTER-INTERNALS.md` — add 4b-i entry in Fragment handling log (SelectStatement, QuerySpecification) and Progress log. Update "Visitor entry points" section.

### Implementation sequence

Each step leaves build green. Do not proceed if a step doesn't build.

1. **Add `ClauseBodyEmitter`** with `WriteClauseBody(emitter, generator, fragment)`:
   - `generator.GenerateScript(fragment, out var text)`.
   - Trim trailing `\r\n`.
   - Split on `\n`.
   - Strip leading whitespace per line (generator's own indent — our scope indents via flush).
   - First non-empty line: `emitter.Write(stripped)`.
   - For each subsequent line: `emitter.NewLine(); emitter.Write(stripped)`.
   - Preserve empty lines as bare `NewLine()` calls (matter for CASE etc. in later slices; harmless in 4b-i).
   - Build check.

2. **Override `QuerySpecification`** in `TSqlFormatterVisitor`. Open `BeginClauseScope`. For each non-null clause in canonical order:
   - `WriteClauseKeyword("SELECT")` then emit `TopRowFilter` (if present) + `UniqueRowFilter` (DISTINCT) + select elements via generator scaffold → `ClauseBodyEmitter.WriteClauseBody` for the composed body. Simpler: generate text for each `SelectElement`, join with `, `, write as body. Even simpler for 4b-i: `generator.GenerateScript(q.SelectElements-as-IEnumerable, …)` isn't an option, so render the whole `QuerySpecification`, strip lines until SELECT's body, extract. **Simplest**: for each clause, construct a synthetic mini-fragment and generate. Too fiddly. **Chosen approach**: render the whole `QuerySpecification` via generator once, then our visitor emits *just the clause keywords* and reuses the generator's body text per clause by pattern-matching line prefixes. This is brittle. **Alternative chosen approach (preferred)**: call `generator.GenerateScript(clauseChild, out text)` on each clause fragment (`FromClause`, `WhereClause`, etc. — each is its own TSqlFragment). For SELECT, `SelectElements` is a list; emit each via generator and join with `", "` for single-line, or split for multi-line. **Decision for 4b-i**: use the "per-clause-fragment generator" approach. Clauses in ScriptDom *are* TSqlFragments (FromClause, WhereClause, GroupByClause, HavingClause, OrderByClause); the generator renders each. For SELECT's column list, render each SelectElement individually, join with `", "`, and let 4b-iii handle multi-column layout.
   - `WriteClauseKeyword("FROM")` then `ClauseBodyEmitter.WriteClauseBody(emitter, generator, q.FromClause)` — note: generator on a `FromClause` fragment emits `FROM xyz`; we need just the body. Options: (a) generator on the `FromClause.TableReferences[0]` (if single) — works for 4b-i's simple cases; multi-table-reference is rare (old-style joins) — defer. (b) generator on the whole FromClause, then strip the leading `FROM` keyword. Cleaner: (a) — iterate `TableReferences`, render each, join. For 4b-i: `q.FromClause.TableReferences.Count == 1` path only; if multiple, fall through to `EmitFragmentDefault(q)` for the whole QuerySpec (give up alignment on this statement).
   - `WHERE`: body is `q.WhereClause.SearchCondition` (a BooleanExpression) — render via generator, feed to `WriteClauseBody`.
   - `GROUP BY`: body is `q.GroupByClause.GroupingSpecifications` — render each, join with `", "`.
   - `HAVING`: body is `q.HavingClause.SearchCondition` (if HavingClause is present — check ScriptDom; older ScriptDom has `HavingClause.SearchCondition`).
   - `ORDER BY`: body is `q.OrderByClause.OrderByElements` — render each, join.
   - Scope disposes; flushed output lands in the outer StringBuilder.
   - Build check after each clause is wired. Four test-driven iterations.

3. **Override `SelectStatement`** in `TSqlFormatterVisitor`. Body:
   - If `s.WithCtesAndXmlNamespaces != null`: `EmitFragmentDefault(s.WithCtesAndXmlNamespaces)` + `NewLine()` (CTE formatting is 4b-iii).
   - `s.QueryExpression.Accept(this)` — dispatches to QuerySpecification override if that's the type, else falls through to base `TSqlFragmentVisitor.ExplicitVisit(<other>)` which we haven't overridden → emits nothing. **Fix**: if `s.QueryExpression is QuerySpecification qs`, call `qs.Accept(this)`; else `EmitFragmentDefault(s.QueryExpression)`. This keeps BinaryQueryExpression and other query types rendering via generator until 4b-iv.
   - If `s.OrderByClause != null` (top-level, outside QuerySpec) — render via generator, aligned as a one-off clause? Actually: this OrderByClause is the one on `SelectStatement` for UNION queries. In simple `SELECT ... ORDER BY x`, the OrderByClause is on QuerySpecification, not SelectStatement. For 4b-i: if `s.OrderByClause != null`, render via `EmitFragmentDefault(s.OrderByClause)` after the QueryExpression. Aligns with 4b-iv's BinaryQueryExpression scope.
   - `ForClause`, `OptimizerHints`, `Into`: `EmitFragmentDefault` each if present.
   - Build check.

4. **Tests**. Create `Tests/QuerySpecificationFormattingTests.cs` with facts enumerated in "Tests" below. Each test calls `ScriptDomFormatter.Format(input, new FormatterOptions())` and asserts substring-contains or exact-equals as appropriate. All six asserting the alignment holds on the outer keywords.

5. **Harness**. Run `dotnet run --project Tools/FormatterRegression/FormatterRegression.csproj -f net10.0`. Record new canonical-match count in FORMATTER-INTERNALS.md's Progress log entry for 4b-i. Expect the MERGE regressions (3) to persist; expect canonical-match count to shift — some files that were 48/48 matches may now differ because 4b-i reformats them with alignment. That is fine: the harness compares *canonical* forms (re-parsed AST equality), so alignment changes don't cause new mismatches as long as re-parse succeeds. If re-parse fails on any previously-passing file, that *is* a regression — stop and investigate.

6. **Docs**. Add to FORMATTER-INTERNALS.md:
   - Fragment handling log entry for `SelectStatement` (one paragraph: what it is, how we emit it, taste decisions).
   - Fragment handling log entry for `QuerySpecification` (one paragraph).
   - Progress log entry for 4b-i with harness number.
   - Update "Visitor entry points" table to show these are overridden now.

7. **Launch the app.** Rebuild, kill any running instance, `dotnet run -f net10.0`. Manual check: the screenshot query renders with aligned outer keywords. Record the actual output in the Progress log (so 4b-ii has a before-state to compare against).

### Test cases (`Tests/QuerySpecificationFormattingTests.cs`)

All tests use `ScriptDomFormatter.Format(input, new FormatterOptions())`. Each asserts the expected alignment shape on outer keywords; for tests exercising subqueries, only the outer alignment is asserted (not the inner shape — that's 4b-ii).

1. **`Format_SelectOnly_AlignsSingleKeyword`**
   - Input: `"SELECT 1"`
   - Expected output (exact): `"SELECT 1\n"`
   - Proves single-clause scope emits without padding.

2. **`Format_SelectFrom_AlignsTwoKeywords`**
   - Input: `"SELECT * FROM dbo.Employees"`
   - Expected (exact): `"SELECT *\n  FROM dbo.Employees\n"`
   - maxKw = 6 (SELECT), FROM padded by 2.

3. **`Format_SelectFromWhere_AlignsThreeKeywords`**
   - Input: `"SELECT * FROM t WHERE x = 1"`
   - Expected (exact): `"SELECT *\n  FROM t\n WHERE x = 1\n"`

4. **`Format_FullClauseSet_AlignsAllKeywords`**
   - Input: `"SELECT a FROM t WHERE x = 1 GROUP BY y HAVING COUNT(*) > 1 ORDER BY z"`
   - Assert: output starts with `"SELECT "`, contains `"GROUP BY "` with appropriate leading spaces (maxKw = 8 for `GROUP BY` and `ORDER BY`), re-parses cleanly, preserves all six clauses in order.

5. **`Format_WhereWithSubquery_OuterAlignsInnerMayNot`** *(deliverable proof)*
   - Input: `"SELECT * FROM [dbo].[Employees] WHERE id IN (SELECT * FROM [dbo].[Employees])"`
   - Assert: output re-parses, first line is `"SELECT *\n"`, second line starts with `"  FROM "`, third line starts with `" WHERE "`. Do **not** assert inner subquery layout (that's 4b-ii).

6. **`Format_MultiColumn_OuterStillAligns`**
   - Input: `"SELECT col1, col2, col3 FROM t"`
   - Assert: output starts with `"SELECT col1, col2, col3\n"` (single-line body via generator) and second line is `"  FROM t\n"`. Multi-line column layout is 4b-iii.

7. **`Format_ReParsesToSameAst`**
   - Input: canonical multi-clause query.
   - Assert: `Reparse(input).Statements.Count == Reparse(output).Statements.Count`; walk both ASTs, compare serialized forms via generator. Proves the alignment work didn't change semantics.

### FORMATTER-INTERNALS.md additions for 4b-i

Append to "Fragment handling log":

```
### SelectStatement (4b-i)
Dispatches to QueryExpression. If QuerySpecification, delegates to that override (aligned
clauses). Otherwise falls through to EmitFragmentDefault. Top-level OrderByClause (the one on
SelectStatement for UNION queries), ForClause, OptimizerHints, Into, WithCtesAndXmlNamespaces
currently fall through to EmitFragmentDefault; CTE and UNION formatting arrive in 4b-iii/4b-iv.

### QuerySpecification (4b-i)
Opens a ClauseScope. Emits SELECT / FROM / WHERE / GROUP BY / HAVING / ORDER BY keywords via
WriteClauseKeyword in canonical order, right-aligned to the widest keyword present.
Clause bodies rendered via Sql170ScriptGenerator on the per-clause fragment
(FromClause.TableReferences, WhereClause.SearchCondition, etc.) and fed into the scope via
ClauseBodyEmitter.WriteClauseBody (splits on \n, strips generator's leading whitespace per
line, emits via Write+NewLine so continuation-line handling in the buffer applies). Limitation:
FROM with multiple TableReferences (old-style joins) falls through to EmitFragmentDefault
(whole QuerySpec) for the statement — modern joins are 4b-iii. Subqueries in clause bodies
render as generator output (not broken to block) in 4b-i; 4b-ii replaces that.
```

Append to "Progress log":

```
### 4b-i — (date)
- SelectStatement / QuerySpecification overridden. Outer clause keywords now right-align in
  the editor on simple SELECT...FROM...WHERE queries.
- Clause bodies still come from Sql170ScriptGenerator per-clause-fragment (deferred to 4b-iii).
- Subqueries in clause bodies still render inline via generator (deferred to 4b-ii).
- Tests: 7 new facts in Tests/QuerySpecificationFormattingTests.cs. All pass.
- Regression harness: <N> canonical matches / <M> parse failures. Compare to 4a's 48/5 baseline.
- Screenshot-query output (for 4b-ii to improve): <paste actual output here>
```

### Exit criteria for 4b-i

**Build**: `dotnet build -f net10.0` on all three projects — no errors. Warnings no worse than 4a baseline (31).

**Tests**: `dotnet test -f net10.0` — 45/45 or similar (38 from 4a + 7 new). All new tests pass.

**Containment (D4)**: `grep -rn "public " Services/Formatting/Visitor/` — no public types. `grep -rn "using SqlVersionControl.Services.Formatting.Visitor" --include="*.cs"` — only `Services/Formatting/` files reference the namespace.

**Runtime**: App launches. The screenshot query (`SELECT * FROM [dbo].[Employees] WHERE id IN (...)`) formats with outer `SELECT` / `FROM` / `WHERE` right-aligned. Output re-parses. Toggle-off produces byte-identical Hogimn output (unchanged from 4a).

**Harness**: runs without the harness itself crashing. No *new* parse-failure regressions beyond 4a's 3 MERGE-variant baselines.

**Docs**: FORMATTER-INTERNALS.md Fragment handling log has `SelectStatement` + `QuerySpecification` entries; Progress log has a 4b-i entry; "Visitor entry points" table is updated. FORMATTER-OVERHAUL.md is unchanged.

### Budget

Half a day to one day. If 4b-i exceeds two days, something is wrong — stop and re-scope. The most likely trap: per-clause-fragment generator calls don't work for some clause type; if so, fall back to generator for the whole statement for that case and document in FORMATTER-INTERNALS.md.

---

## 4b-ii — Subquery alignment

### Purpose

Nested `BeginClauseScope` works. `ScalarSubquery`, `InPredicate` with a subquery, and `ExistsPredicate` break to a multi-line indented block instead of rendering inline via generator. Target shape for `WHERE id IN (...)` per D9:

```
 WHERE id IN (
          SELECT *
            FROM [dbo].[Employees]
      )
```

- `(` stays on the `WHERE` line (part of WHERE's body).
- Newline after `(`.
- Inner `QuerySpecification.Accept(this)` runs with a **fresh** `ClauseScope`, at `outerIndent + 1 level` (per `IndentSize`).
- Inner SELECT / FROM align locally (inner `maxKw` is independent of outer).
- Closing `)` on a new line, at `outerIndent` (so it visually closes back at the WHERE body column).

### Scope — in

1. **Emitter**: stack of clause buffers.
   - `BeginClauseScope` no longer throws if one is active. Pushes a new buffer; new buffer captures current indent level.
   - Subscope dispose: renders subscope content to a string, writes that string into the *parent* buffer's current line body. Each rendered line after the first becomes a body-continuation in the parent (parent's flush handles indent).
   - Alternative: subscope dispose writes directly to the emitter's `_sb` with its own indent; parent buffer just records a marker "raw block inserted here". **Decide during implementation which is cleaner.**
2. **Visitor overrides** for the three subquery-bearing expression types:
   - `ScalarSubquery` (subquery in expression position): emit `(`, NewLine, +1 indent, open nested ClauseScope, `sub.QueryExpression.Accept(this)`, close scope, NewLine, dedent, `)`.
   - `InPredicate` (when `Subquery != null`): emit `<expr> IN (`, NewLine, +1 indent, nested scope, emit subquery, close, NewLine, dedent, `)`. When `Subquery == null` (values list), fall through to generator.
   - `ExistsPredicate`: emit `EXISTS (`, same pattern.
3. **`SelectStatement` / `QuerySpecification`** overrides from 4b-i continue to work unchanged — nested recursion into subquery just opens another scope and flushes up through the stack.
4. **Tests** for nested alignment: the screenshot query, `EXISTS` variant, `ScalarSubquery` in `SELECT` column, `ScalarSubquery` in `WHERE` comparison. Four to six facts.

### Scope — not in 4b-ii

- Joins — 4b-iii.
- CTE — 4b-iii.
- CASE — 4b-iv.
- UNION — 4b-iv.
- Subquery in `FROM` (derived table, `JoinTableReference` wrapping `QueryDerivedTable`) — 4b-iii with joins.

### Implementation notes

- The emitter stack: a `Stack<ClauseBuffer>` field. `BeginClauseScope` pushes; scope disposer pops; if stack still has entries after pop, flush popped buffer into new top via `Write` of each rendered line (first line attached to current body, subsequent as continuations). If stack is empty, flush to `_sb` directly.
- Relative-indent of subscope: captured at `BeginClauseScope`. If subscope is opened after an explicit `Indent()`, captured indent is `outer + 1`.
- The visitor must call `Indent()` *before* `BeginClauseScope` when it wants the subscope at an inner indent level — otherwise the subscope captures the outer indent.

### Test cases

1. `Format_WhereInSubquery_InnerAligns`: the screenshot query. Exact-equals check on full output.
2. `Format_WhereExistsSubquery_InnerAligns`: `SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)`.
3. `Format_ScalarSubqueryInSelect_InnerAligns`: `SELECT a, (SELECT MAX(x) FROM u) AS m FROM t`.
4. `Format_ScalarSubqueryInWhere_InnerAligns`: `SELECT * FROM t WHERE a = (SELECT MAX(a) FROM t)`.
5. `Format_DoublyNestedSubquery`: subquery inside subquery; asserts both inner scopes align locally.

### Exit criteria (delta vs 4b-i)

- The screenshot query output is now the D9 target shape (exact-equals assertion in test 1).
- Emitter `SqlEmitterTests` gains ≥3 nested-scope tests; the `ClauseScope_NestedScopeThrows_4bWillRelaxThis` test is **removed** (no longer throws).
- FORMATTER-INTERNALS.md's 4a limitation note about "single scope at a time" updated to describe the stack-based model.

---

## 4b-iii — Joins, column lists, CTEs

> **Split into two sub-slices** (decided 2026-04-25 after 4b-ii landed; the four-sub-feature scope is too large for one session). 4b-iii-a covers FROM body (joins + derived tables); 4b-iii-b covers list wrap + CTEs and retires `ClauseBodyEmitter`. 4b-iii-a plan: `~/.claude/plans/4b-iii-a-joins-derived-tables.md`. The "Key design decisions to land in 4b-iii (not pre-decided here)" subsection below is authoritative for both sub-slices; 4b-iii-a commits to decisions on joins + derived tables, 4b-iii-b picks up list wrap + CTE.

### Purpose

Real structural formatting for the things that make real sprocs readable:

- **Joins**: each JOIN keyword on its own line, aligned within its own local scope (or within the parent FROM's scope — decide in the slice). `ON` clause indented under the JOIN. Multiple joins stack vertically.
- **Column lists**: `SELECT a, b, c` stays single-line if short; wraps to multi-line with trailing-comma layout (per D3) when long.
- **`GROUP BY` / `ORDER BY` elements**: same multi-line wrap rule.
- **CTEs**: `WithCtesAndXmlNamespaces` renders as `WITH cte_name AS (\n    ...\n), cte_name2 AS (\n    ...\n)\nSELECT ...`. Each CTE body gets its own nested scope.

### Scope — in

Visitor overrides:
- `FromClause` with multiple TableReferences or non-trivial join structure.
- `JoinTableReference` family: `QualifiedJoin`, `UnqualifiedJoin`, `JoinParenthesisTableReference`, `CrossApplyTableReference`, `OuterApplyTableReference`, `PivotedTableReference` (pivot may defer).
- `QueryDerivedTable` (subquery in FROM).
- `SelectScalarExpression` list handling in QuerySpecification — replace 4b-i's "render via generator and comma-join" with our own multi-line layout honoring `MaxLineLength` and `CommaStyle`.
- `GroupingSpecification` list handling.
- `ExpressionWithSortOrder` list handling (ORDER BY elements).
- `CommonTableExpression` + `WithCtesAndXmlNamespaces`.

### Scope — not in 4b-iii

- CASE expression layout.
- UNION / INTERSECT / EXCEPT layout.
- PIVOT / UNPIVOT bodies.

### Key design decisions to land in 4b-iii (not pre-decided here)

- **Where does `MaxLineLength` enter?** Proposed: a helper that renders a list of fragments as comma-joined single-line; if that exceeds `MaxLineLength`, re-emit as one-element-per-line with trailing commas. Decide how to measure (current column when the list starts vs absolute column).
- **Join scope nesting**: does each JOIN open its own ClauseScope (to right-align `LEFT OUTER JOIN` / `INNER JOIN` / `ON` keywords within the JOIN block), or do all JOINs share the FROM's scope? Compare output shapes on real GittyExport sprocs during the slice.
- **CTE body indent**: +1 level from the `WITH`, or the CTE body gets its own statement-level scope at the outer statement's indent?

These decisions land *during* 4b-iii against real corpus diffs, not pre-committed here. Record the chosen design in FORMATTER-INTERNALS.md as part of the slice.

### Tests

- Two-table JOIN with ON.
- Three-table LEFT JOIN chain.
- CROSS APPLY.
- Long column list wrapping.
- Single CTE (`WITH cte AS (…) SELECT …`).
- Chained CTEs.
- Regression harness: expect *significant* canonical-match changes — compare pre/post counts.

---

## 4b-iv — CASE and UNION

### Purpose

Two last major visible-in-real-sprocs constructs:

- `SimpleCaseExpression` / `SearchedCaseExpression`: `CASE ... WHEN ... THEN ... ELSE ... END` with each `WHEN`/`THEN` on its own line, aligned. Nested CASE handled recursively.
- `BinaryQueryExpression` (UNION / INTERSECT / EXCEPT): each arm is a `QuerySpecification`; they stack vertically with the set operator between them, aligned.

### Scope — in

- `SimpleCaseExpression`, `SearchedCaseExpression`, `CaseExpression` (abstract parent — the two concrete types are the real overrides).
- `BinaryQueryExpression`.

### Scope — not in 4b-iv

- Anything in 4c+: DML, DDL, control flow. If we find that CASE inside UPDATE SET needs more work, record in FORMATTER-INTERNALS.md for 4c.

### Tests

- Simple CASE with two branches.
- Searched CASE with ELSE.
- Nested CASE.
- Simple UNION ALL between two SELECTs.
- UNION mixed with nested subquery.
- Regression harness final 4b count — after 4b-iv, most of the GittyExport corpus should canonical-match. Record the new baseline.

---

## Across all four slices

### The containment rule for the generator scaffold (D8 restated)

Each slice removes generator-fallback coverage for *at least* one fragment type. After 4b-iv lands:

| Construct | Handled by | Generator fallback? |
|---|---|---|
| SelectStatement / QuerySpecification / clauses | Our overrides | No |
| Subqueries | Our overrides | No |
| Joins / CTEs / column lists | Our overrides | No |
| CASE / UNION | Our overrides | No |
| DML (INSERT/UPDATE/DELETE/MERGE) | Generator (4c replaces) | **Yes** |
| DDL (CREATE/ALTER ...) | Generator (4d replaces) | **Yes** |
| Control flow (BEGIN/END/IF/WHILE/TRY/CATCH) | Generator (4e replaces) | **Yes** |
| PIVOT / APPLY details | Generator (4f replaces) | **Yes** |

After 4g finishes, the generator and `EmitFragmentDefault` **must be gone**. If a 4b–4f slice finds it "easier" to keep the generator around for some edge case, that is a bug.

### FORMATTER-OVERHAUL.md is still frozen

Do not edit that doc during any 4b slice. Record all design decisions in FORMATTER-INTERNALS.md under "Known deviations" or in the Fragment handling log.

### Sequencing between slices

- Separate PR per slice.
- Separate session per slice (reset context).
- Each PR includes: code, tests, FORMATTER-INTERNALS.md updates, regression harness result recorded, a note in Progress log.
- User approval between slices — do not chain two slices in one session without an explicit override from the user (like "do 4b-i and 4b-ii back-to-back").

### Testing expectations

Each slice adds facts under `Tests/`, keeping per-slice tests in their own file (`Tests/QuerySpecificationFormattingTests.cs`, `Tests/SubqueryFormattingTests.cs`, `Tests/JoinAndCteFormattingTests.cs`, `Tests/CaseAndUnionFormattingTests.cs`). Shared regression invariants (output re-parses, etc.) can be helper methods in a single `Tests/FormatterTestHelpers.cs`.

After 4b-iv, the test count should be somewhere around 70–90.

---

## Critical files touched during 4b

- `Services/Formatting/Visitor/TSqlFormatterVisitor.cs` — grows substantially across slices. Each `ExplicitVisit` override is a method.
- `Services/Formatting/Visitor/SqlEmitter.cs` — 4b-ii adds stack support.
- `Services/Formatting/Visitor/ClauseBodyEmitter.cs` — new in 4b-i; may grow or get absorbed back into the visitor as fragment types get their own overrides (the helper's main purpose is feeding generator output into a clause scope, which becomes less necessary as coverage grows).
- `docs/FORMATTER-INTERNALS.md` — grows each slice.
- `Tests/…FormattingTests.cs` — one file per slice.

## Critical files NOT touched during 4b

- `Services/SqlFormatterService.cs` — public surface unchanged.
- `Services/Formatting/LegacyHogimnFormatter.cs` — legacy engine untouched.
- `Services/Formatting/ScriptDomFormatter.cs` — parse-and-dispatch unchanged.
- `Services/Formatting/FormatterOptions.cs` — no new fields added in 4b (all options are in place from 4a; tuning their defaults happens after 4b-iv completes, against the full corpus).
- `FORMATTER-OVERHAUL.md` — frozen.
- `Views/` — no UI changes.
- `Settings*` — no new settings.

---

## After 4b-iv

- All `QuerySpecification`-scoped constructs format via our visitor overrides.
- Generator fallback remains for DML / DDL / control-flow / PIVOT — 4c–4f ranges.
- FORMATTER-INTERNALS.md has detailed entries for every fragment type touched.
- Record the canonical-match pass rate against the corpus in the Progress log.
- The next step is 4c (DML). Do **not** start 4c in the same session that completes 4b-iv.
