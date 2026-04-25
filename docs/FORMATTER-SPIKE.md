# Formatter Path A Spike — Findings & Recommendation

**Date:** 2026-04-24
**Scope:** Sequencing step 3 of the formatter revamp. Half-day research task: run `Sql170ScriptGenerator` against a representative corpus, answer three go/no-go questions, recommend Path A+overrides or Path B.

## Corpus

11 files under `scripts/formatter-test-corpus/`:

- 6 handwritten stress cases covering the doc's edge-case list — nested subquery (the screenshot pathology), 3-level CTE, CASE with 5 branches, MERGE with all three WHEN clauses, sproc with TRY/CATCH-in-WHILE-in-IF, comments in every position.
- 4 real SQL Server-scripted stored procedures pulled from `GittyExport/localhost_1433/TestDB/StoredProcedures/` — staffing report, update salary, search employees, get-by-department.
- 1 CREATE TABLE from the same source.

**Note on "real":** these are from the local TestDB, not from REPOSITORY DB (no local `sqlcmd` / `mssql-cli` available, and REPOSITORY DB isn't scripted into `GittyExport/`). They are real SQL-Server-generated scripts and exercise real patterns (joins, STRING_AGG, parameters with defaults, TRY/CATCH around transactions), but they are not hairy WMS-scale code. **Recommendation:** drop 2-3 representative REPOSITORY DB sprocs (e.g. `usp_get_sorter_lane_rpick`) into the corpus and re-run the spike before the decision lands in a PR. The per-file outputs under `scripts/formatter-test-corpus/reports/spike/` are gitignored so they'll regenerate.

## Method

`Tools/FormatterRegression/Program.cs --spike` writes three files per corpus entry to `reports/spike/<name>/`:

- `original.sql` — unchanged input
- `hogimn.sql` — `LegacyHogimnFormatter.Format` (floor; context only, **not** the comparison bar)
- `pathA.sql` — `Sql170ScriptGenerator` direct with these options:
  - `KeywordCasing = Uppercase`
  - `IncludeSemicolons = false`
  - `NewLineBefore{From,Where,GroupBy,Having,OrderBy}Clause = true`
  - `AlignClauseBodies = true`
  - `IndentationSize = 4`

Judgment framing: **`pathA.sql` vs `original.sql`**, not vs Hogimn.

## The Three Questions — Answers

### Q1 — Is the default output shape tolerable?

**No, not without significant overrides.** Parse succeeded on 11/11 files. Several categories of output are unacceptable as-is:

**Wins:**

- **Nesting works.** The screenshot pathology (`WHERE id IN (SELECT ...)` split across statements by the regex) is gone. Path A indents nested SELECTs under their parent clause — this is the entire motivation for ScriptDom showing up as expected:
  ```
  SELECT *
  FROM   [dbo].[Employees]
  WHERE  id IN (SELECT *
                FROM   [dbo].[Employees]);
  ```
- **Keyword casing consistent, CTE structure preserved, control flow (IF / WHILE / TRY/CATCH) preserved.**

**Failures:**

1. **CASE expressions collapse to one line** even when the original had 5 WHEN branches across 5 lines. From `03-case-with-branches`:
   ```sql
   -- Original:
   CASE
       WHEN e.Salary < 50000 THEN 'Entry'
       WHEN e.Salary BETWEEN 50000 AND 100000 THEN 'Mid'
       WHEN e.Salary BETWEEN 100001 AND 150000 THEN 'Senior'
       WHEN e.Salary BETWEEN 150001 AND 200000 THEN 'Lead'
       ELSE 'Executive'
   END AS SalaryBand
   -- Path A:
   CASE WHEN e.Salary < 50000 THEN 'Entry' WHEN e.Salary BETWEEN 50000 AND 100000 THEN 'Mid' WHEN e.Salary BETWEEN 100001 AND 150000 THEN 'Senior' WHEN e.Salary BETWEEN 150001 AND 200000 THEN 'Lead' ELSE 'Executive' END AS SalaryBand
   ```
   The Path A line is 220+ characters.

2. **MERGE WHEN branches collapse to one line.** From `04-merge`:
   ```sql
   -- Path A, line 12:
   WHEN NOT MATCHED BY TARGET THEN INSERT (EmployeeId, Salary, EffectiveDate, UpdatedAt) VALUES (src.EmployeeId, src.Salary, src.EffectiveDate, GETDATE())
   ```
   160+ character line. The WHEN MATCHED / WHEN NOT MATCHED / ON clauses also fragment awkwardly across lines (`USING (…) AS src ON … AND …` gets collapsed, `AS tgt` floats to its own line prefixed with a stray space).

3. **JOIN layout splits across three lines.** From `07-real-sproc-staffing-report`:
   ```sql
   FROM     dbo.Projects AS p
            LEFT OUTER JOIN
            dbo.ProjectAssignments AS pa
            ON pa.ProjectId = p.ProjectId
   ```
   Keyword on line 1, table on line 2, ON on line 3 — harder to scan than Hogimn's single-line JOIN.

4. **CREATE PROCEDURE parameter lists collapse.** Multi-line parameter declarations squash onto one line even when the original was declaratively clear (one param per line).

5. **Keyword right-alignment padding.** `SELECT`, `FROM`, `WHERE`, `ORDER BY` get whitespace-padded to align their right edges, producing columns of weird trailing space (`SELECT   ... FROM     ... WHERE    ...`). Style preference, but it's not matching the surrounding tool aesthetic of the codebase.

### Q2 — How bad is comment preservation?

**Catastrophic. 25 → 0. Complete loss, 100%.**

| Corpus file | Original comment count | Path A comment count |
|---|---|---|
| 01-nested-subquery | 2 | 0 |
| 02-nested-cte | 1 | 0 |
| 03-case-with-branches | 1 | 0 |
| 04-merge | 1 | 0 |
| 05-sproc-control-flow | 1 | 0 |
| 06-comments-everywhere | 16 | 0 |
| 10-real-sproc-get-by-dept | 3 | 0 |
| **Total** | **25** | **0** |

The `06-comments-everywhere` file was specifically crafted with comments in every position the doc's attachment rules cover: leading block above statement, trailing same-line on columns, leading before column, inline mid-expression, between FROM and JOIN, after ON, in WHERE predicates, disabled-predicate `-- AND …`, trailing after semicolon, footer. Path A drops all 16.

**This is the go/no-go gate the doc explicitly called out:** "Path A's comment behavior is a go/no-go gate we can't fully predict until tested." The gate has failed.

### Q3 — Can SqlScriptGenerator be subclassed to fix our objections in ~200 lines?

**No. The minimum viable override set is roughly 1500–2500 lines.**

The ~200-line hope assumed the generator exposed hooks for taste-level formatting (CASE layout, JOIN layout, parameter lists). It mostly doesn't — `Sql170ScriptGenerator` inherits from `SqlScriptGenerator`, whose protected/virtual surface is low-level (emit keyword, emit identifier, generate script for this fragment). To fix CASE/MERGE/JOIN you end up intercepting `GenerateScript(TSqlFragment)` per-type and re-implementing emission, which is exactly what Path B is.

The bigger cost is comment preservation. Comments aren't part of the AST — they live in `ScriptTokenStream` and are simply not consulted by `SqlScriptGenerator`. Re-integrating them requires:

- A comment-attachment pass that classifies each token against the four cases from the doc
- An emitter that knows, at each fragment boundary, which attached comments to emit and at what indent
- A custom `StringBuilder`/writer threaded through the generator's emission path

This is not an override layer on top of `Sql170ScriptGenerator`. It's a parallel emitter. Once built, Path A's "free" default emission for DML/DDL stops being free because you've replaced the emission mechanism.

**Rough LOC estimates (including tests & options plumbing):**

- Path A + overrides to reach "acceptable output w/ comments preserved": **~1500–2500 LOC**, centered on a custom emitter + CASE/MERGE/JOIN overrides + comment attacher. Fighting the generator at every turn.
- Path B from-scratch visitor covering the doc's 40–60 fragment types + comment attacher: **~2500–4000 LOC**, cleaner separation, no generator to fight.

Path A saves somewhere between 500 and 1500 LOC in theory — but those saved LOC are the generator's existing emission for fragments where we agree with its defaults, which is a minority of real-world cases. Path A also carries a debugging tax: when output is wrong, the generator is the black box and you're guessing which of its private methods to override.

## Recommendation: **Path B**

### Why

1. **Comment preservation — the gate — has failed for Path A.** The only path to fixing it is a parallel emitter, which is Path B.
2. **CASE, MERGE, and JOIN layouts are unacceptable in default Path A output and require custom logic.** That logic is easier to write as visitor overrides than as `SqlScriptGenerator` subclass overrides because `TSqlFragmentVisitor` was designed for this, and `SqlScriptGenerator` wasn't.
3. **The perceived Path A win (low effort) doesn't survive the evidence.** Fighting the generator's low-level surface is costlier than the higher-level ~1000 LOC the spike hoped would save.
4. **Nesting works cleanly in both paths** — the ScriptDom AST is the real win. Which emitter sits on top of it is the decision, and Path B's emitter gives us control over comments, CASE, MERGE, JOIN, and every other taste knob without a generator in the way.

### What this unlocks

The doc's step 4 (visitor implementation, split into 4a–4g) proceeds as specified. The FormatterOptions TBD fields (`IndentSize`, `Uppercase`, `MaxLineLength`, `CommaStyle`) get filled in during step 4a from direct observation of what looks good as the skeleton emits.

### Scope-shaping findings from the spike

- **`CommaStyle`**: Path A's right-padded keyword alignment is an example of "unusual default" to reject — Lookout's taste is trailing commas, columns left-aligned under each other. Record this as the Path B default when 4a fills in the TBDs.
- **JOIN layout**: Path B visitor override table says "Join keyword on new line at current indent, ON on next line indented +1" — the spike confirms we definitely do not want the Path-A three-line split.
- **CASE**: Path B default is multi-line CASE/WHEN/THEN/ELSE aligned. Path A's collapse is evidence this default is the right one.
- **MERGE**: Each WHEN MATCHED / WHEN NOT MATCHED branch gets its own line. Path A's collapse confirms this default.
- **Comment attachment**: confirmed as the load-bearing component. Build it in 4a as hook points (no-op) so 4b-4f emission code is comment-ready by the time 4g fills in the four-case logic.

### Sanity check todo before committing the decision to the repo

Drop 2–3 representative REPOSITORY DB sprocs (e.g. `usp_get_sorter_lane_rpick`) into `scripts/formatter-test-corpus/` and re-run `--spike`. If Path A suddenly looks much better on that code than on this corpus, reconsider. Unlikely based on the category of failures observed, but worth the five minutes.

## Next step

Proceed to sequencing **step 4a** — visitor skeleton + comment-attacher hook points + filling in the four TBD `FormatterOptions` defaults from what looks good against the corpus.
