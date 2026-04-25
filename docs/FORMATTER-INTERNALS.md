# Formatter Internals

Companion to [FORMATTER-OVERHAUL.md](FORMATTER-OVERHAUL.md). That doc is the strategic record
(frozen during 4a–4g); this one is the living implementation record across those steps.

## File layout

```
Services/Formatting/
├── LegacyHogimnFormatter.cs       # Legacy engine — untouched during 4a-4g
├── ScriptDomFormatter.cs          # Parse + dispatch to visitor; timeout + fallback
├── FormatterOptions.cs             # Configurable formatting choices
└── Visitor/
    ├── TSqlFormatterVisitor.cs    # TSqlFragmentVisitor subclass (internal)
    ├── SqlEmitter.cs              # StringBuilder + indent state (internal)
    ├── CommentAttacher.cs         # Token-stream → fragment association (internal)
    ├── CommentEmission.cs         # Emits attached comments at hook points (internal)
    └── SelectionParseStaircase.cs # TSqlScript / ParseStatementList / ParseExpression (internal)
```

`ClauseBodyEmitter.cs` was a 4b-i helper for feeding multi-line generator text into the
active clause scope; retired in 4b-iii-b. The inline `EmitMultiLineGeneratorBody` that
replaced it is also gone (4b-iv) — once `BooleanBinaryExpression` got its own override, the
remaining `EmitSearchConditionBody` default-case fragments all render single-line via
generator, so the helper inlined to a 2-line generator-and-write.

All `Visitor/*` types are `internal`. Tests reach them via
`[assembly: InternalsVisibleTo("SqlVersionControl.Tests")]` in `Properties/AssemblyInfo.cs`.

## Public contract

Exactly one entry point: `SqlFormatterService.Format(string) : string`. No other type in this
namespace is part of the public API. `ScriptDomFormatter`, `LegacyHogimnFormatter`, and
`FormatterOptions` stay public to preserve the pre-revamp surface used by the dispatcher and
by any future alternative entry, but nothing outside `Services/Formatting/` should call them
directly.

## FormatterOptions defaults — decided values and why

| Field | Value | Rationale |
|---|---|---|
| `IndentSize` | `4` | Matches SSMS / `GittyExport` sprocs / Hogimn output. Any other value would be gratuitously different from what the user reads daily. |
| `Uppercase` | `true` | Every real sproc in `GittyExport` uses uppercase keywords. Matches Hogimn's `Uppercase(true)` config. |
| `MaxLineLength` | `120` | Hogimn's 80 caused excessive vertical spreading on real sprocs with long `[schema].[table].[column]` identifiers during the spike. 120 fits typical developer monitors at normal font size and aligns with modern editor conventions. |
| `CommaStyle` | `Trailing` | `GittyExport/localhost_1433/TestDB/StoredProcedures/*.sql` uses trailing commas. Path A's leading-comma with keyword-column alignment was rejected in the spike. |
| `AlignAndOrAtStart` | `true` | Matches the visitor override table in `FORMATTER-OVERHAUL.md`. |
| `IncludeSemicolons` | `true` | **Flipped from false** (D2 in `4A-PLAN.md`). SQL Server increasingly warns/errors on missing semicolons (`THROW`, `;WITH` CTEs); `Sql170ScriptGenerator` emits them even when the option is false; backward-compat with Hogimn's strip-semicolons behavior is not a goal post-toggle. |
| `AlignClauseBodies` | `true` | **Added in 4a** (see "Clause-keyword right alignment" below). Right-pads clause keywords (`SELECT` / `FROM` / `WHERE` / `AND` / …) so the rightmost keyword character lands in the same column within a clause scope; bodies therefore form a clean left edge for vertical scanning. Set to `false` to emit left-aligned keywords with no padding. |

## Visitor entry points (as of 4a)

Overridden:
- `ExplicitVisit(TSqlScript)` — iterates `Batches`, emits `GO` separator between batches (not before first, not after last).
- `ExplicitVisit(TSqlBatch)` — iterates `Statements`, calls comment-emission hooks at statement
  boundaries, emits each statement via `EmitFragmentDefault`, blank line between statements.
- `ExplicitVisit(SelectStatement)` *(4b-i)* — emits optional CTE via scaffold, dispatches `QueryExpression` to its override if it's a `QuerySpecification` (falls back to scaffold for `BinaryQueryExpression` until 4b-iv).
- `ExplicitVisit(QuerySpecification)` *(4b-i)* — opens a `ClauseScope` and emits `SELECT` / `FROM` / `WHERE` / `GROUP BY` / `HAVING` / `ORDER BY` via `WriteClauseKeyword`. Clause bodies come from `Sql170ScriptGenerator` on per-clause fragments, fed through `ClauseBodyEmitter.WriteBody` (or, in 4b-ii, via the WHERE / HAVING / SELECT-element dispatchers when a top-level subquery shape is detected). Niche flags (`TOP`, `OFFSET`, `FOR`, multi-table old-style FROM, any `JoinTableReference`) fall back to `EmitFragmentDefault` for the whole statement — those paths land in 4b-iii. SelectStatement now pre-checks the same flags and falls back at *its* level (see § Fragment handling log).
- `ExplicitVisit(ScalarSubquery)` *(4b-ii)* — `(` + NewLine + `Indent()` + recurse via `EmitSubqueryQueryExpression` + NewLine + dedent + `)`. No own `BeginClauseScope` — recursion hits `QuerySpecification` which opens one.
- `ExplicitVisit(InPredicate)` *(4b-ii)* — only the `Subquery != null` shape gets break-to-block; values list falls through to generator. Emits LHS (via generator scaffold) + ` IN (` (or ` NOT IN (` if `NotDefined`) + same break-to-block tail as `ScalarSubquery`.
- `ExplicitVisit(ExistsPredicate)` *(4b-ii)* — emits `EXISTS (` + same tail.
- `ExplicitVisit(QualifiedJoin)` *(4b-iii-a)* — INNER / LEFT OUTER / RIGHT OUTER / FULL OUTER JOIN with inline ON (break to own line at +`IndentSize` under the JOIN keyword when rendered condition exceeds `MaxLineLength * 2/3`). Chains render left-associatively via recursion into `FirstTableReference`.
- `ExplicitVisit(UnqualifiedJoin)` *(4b-iii-a)* — CROSS JOIN / CROSS APPLY / OUTER APPLY. Same stacking as QualifiedJoin, no ON. APPLY variants' right-hand side is typically a `QueryDerivedTable` which dispatches back through `EmitTableReferenceBody`.
- `ExplicitVisit(QueryDerivedTable)` *(4b-iii-a)* — subquery in FROM. Break-to-block pattern (`(` + NL + `Indent()` + Accept + NL + `)` + ` AS alias`), mirrors `ScalarSubquery`. Does not open its own `BeginClauseScope` — the inner QuerySpecification does.
- `ExplicitVisit(CommonTableExpression)` *(4b-iii-b)* — `name AS (` + NL + Indent + recurse + NL + `)`. Flat indent per D2; no own clause scope. Optional column list `(col1, col2)` rendered via per-identifier generator scaffold. Uses `_emitter.AtLineStart` to skip a redundant NL before `)` (inner scope's trailing NL survives when flushed directly to `_sb`, unlike the injected-into-parent case).
- `ExplicitVisit(WithCtesAndXmlNamespaces)` *(4b-iii-b)* — `WITH ` at current indent, each CTE dispatched via `Accept`, separated by `",\n"` (trailing comma per D3).
- `ExplicitVisit(SearchedCaseExpression)` *(4b-iv)* — `CASE` on header line; `WHEN <cond> THEN <body>` and `ELSE <body>` at +`IndentSize`; `END` at CASE column. CASE-specific `_caseDepth` counter drives the per-level pad independent of the global `_indentLevel` (so a CASE inside a subquery WHERE doesn't get extra END pad from the subquery's outer Indent).
- `ExplicitVisit(SimpleCaseExpression)` *(4b-iv)* — same shape, with `InputExpression` after `CASE` on header. Each WhenClause's `WhenExpression` is a `ScalarExpression` rather than a `BooleanExpression`.
- `ExplicitVisit(BinaryQueryExpression)` *(4b-iv)* — UNION / UNION ALL / INTERSECT / EXCEPT. Recurses into `FirstQueryExpression` first, then operator + NL + `SecondQueryExpression`. No own clause scope (D2). `bqe.OrderByClause` (inherited from `QueryExpression`, applies to the whole set operation) emits in a one-clause scope at statement indent after both arms (D4).
- `ExplicitVisit(BooleanBinaryExpression)` *(4b-iv)* — AND / OR. Inside a clause scope (`InClauseScope`): operand → NL → `WriteClauseKeyword(AND|OR)` → operand, with each operand routing through `EmitSearchConditionBody` (so subquery shapes break to block, nested BBEs recurse). Outside a clause scope (D3): falls back to `EmitGeneratorRaw` — preserves current behavior for ON / CASE WHEN / scalar contexts; AND/OR-in-ON quirk is left for a future slice.
- `ExplicitVisit(InsertStatement)` *(4c)* — no outer clause scope (D3). Emits `INSERT [INTO|OVER] <target>`, optional `(cols)` at +`IndentSize`, OUTPUT (grammar: before source), then `VALUES` / `SELECT` / `EXEC` source. `TopRowFilter` trips whole-statement fallback (D6).
- `ExplicitVisit(UpdateStatement)` *(4c)* — UPDATE / SET / OUTPUT / FROM / WHERE in one clause scope (D1). Right-align per D7. FROM body reuses `EmitWrappedList` + `EmitTableReferenceBody` (JOINs stack the same as in a SELECT).
- `ExplicitVisit(DeleteStatement)` *(4c)* — DELETE / FROM / OUTPUT / WHERE in one clause scope (D2). Simple form emits `DELETE` as keyword + `FROM <target>` in the body so scope `maxKw` stays at 6 (not 11 from "DELETE FROM"). Extended form (`FromClause != null`) emits DELETE and FROM as separate keyword lines.
- `ExplicitVisit(MergeStatement)` *(4c)* — header scope (`MERGE INTO` / `USING` / `ON`, `maxKw = 10`). WHEN clauses stack at statement indent outside the scope (D4); each WHEN emits `WHEN [NOT] MATCHED [BY TARGET|SOURCE] [AND <cond>] THEN\n<action at +IndentSize>`. OUTPUT (D5 shared dispatcher) lands after all WHENs per grammar. Terminating `;` emitted when `IncludeSemicolons` — required for re-parse; retires the 3 MERGE-variant harness regressions.
- `ExplicitVisit(IfStatement)` *(4e-ii)* — `IF <predicate>` on header line; `ThenStatement` recursed via `EmitConditionalBody` (BEGIN/END body lands at IF column; single-statement body wrapped in `Indent()`). Optional `ELSE` on its own line at IF column. Predicate routes through `EmitConditionalPredicate` — separate from `EmitSearchConditionBody` because IF/WHILE have no clause scope; same dispatch table minus BBE, plus a `BooleanNotExpression` branch so `NOT EXISTS (subq)` decomposes to `NOT ` + recurse. Subquery-bearing predicates break to indented block via the 4b-ii overrides.
- `ExplicitVisit(WhileStatement)` *(4e-ii)* — same shape as IfStatement minus the ELSE branch.
- `ExplicitVisit(CreateViewStatement)` / `ExplicitVisit(AlterViewStatement)` / `ExplicitVisit(CreateOrAlterViewStatement)` *(4d-ii)* — three thin overrides delegating to `EmitViewBody(ViewStatementBody, keywordPrefix)`. Header: keyword + generator-rendered `SchemaObjectName` + optional inline `(col1, col2)` column list (mirrors `CommonTableExpression.Columns` — per-identifier generator scaffold, no wrap) + optional `WITH <opts>` (generator-rendered, comma-joined) + `AS` + `SelectStatement` body via `EmitFragmentDefault` (flat indent — no `Indent()`, matches SSMS) + optional `WITH CHECK OPTION` trailer at col 0. `IsMaterialized` (Synapse materialized views) trips an `EmitGeneratorRaw` fallback — same defensive guard pattern as `BeginEndAtomicBlockStatement`.

Not yet overridden (planned per sub-step):
- **4b** — split into four slices (see docs/4B-PLAN.md); 4b-iii is itself split into a+b:
  - **4b-i**: `SelectStatement`, `QuerySpecification` clause keywords (SELECT / FROM / WHERE / GROUP BY / HAVING / ORDER BY / OFFSET) wired through `BeginClauseScope` + `WriteClauseKeyword`. Clause *bodies* still go through `Sql170ScriptGenerator` in this slice.
  - **4b-ii**: Nested `BeginClauseScope` (subquery alignment); `ScalarSubquery` / `ExistsPredicate` / `InPredicate` with subquery break to indented block.
  - **4b-iii-a**: `JoinTableReference` family (`QualifiedJoin`, `UnqualifiedJoin`), `QueryDerivedTable` (subquery in FROM). Kills the subquery-with-JOINs trailing-clause drop.
  - ~~**4b-iii-b**~~: Landed 2026-04-25. List wrap for SELECT / GROUP BY / ORDER BY / old-style FROM; CTEs; `ClauseBodyEmitter` retired.
  - ~~**4b-iv**~~: Landed 2026-04-25. `SimpleCaseExpression` / `SearchedCaseExpression`, `BinaryQueryExpression` (UNION / INTERSECT / EXCEPT), `BooleanBinaryExpression` (AND / OR inside clause scope). `EmitMultiLineGeneratorBody` retired. Resolved Known limitations #1, #2, #3.
- ~~**4c**~~: Landed 2026-04-25. `InsertStatement`, `UpdateStatement`, `DeleteStatement`, `MergeStatement`. Retired the 3 MERGE-variant harness regressions (48/0/5 → 51/0/2). Also retired the QueryDerivedTable-inside-JOIN indent bug (emitter nested-pop strip-parent-outer fix).
- ~~**4c-ii**~~: Landed 2026-04-25. Pre-parse auto-repair (`SqlPreRepair.Normalize`) — `;WITH` rule. Corpus-driven against `Sorgu/`; 224/264 already parsed, no `;WITH` cases in this corpus, retained as regression guard.
- **4d**: CREATE / ALTER variants by object type. Five sub-slices.
  - ~~**4d-i**~~: Landed 2026-04-25. `CreateProcedureStatement`, `AlterProcedureStatement`, `CreateOrAlterProcedureStatement` via shared `EmitProcedureBody` helper. Bundled with 4e-i (procedure body needs BEGIN/END recursion to be useful). Bundled fixes: `WithCtesAndXmlNamespaces` handling on the four DML overrides; `SqlEmitter.EnsureTrailingSemicolon` on body recursion (control-flow statement terminators).
  - ~~**4d-ii**~~: Landed 2026-04-25. `CreateViewStatement`, `AlterViewStatement`, `CreateOrAlterViewStatement` via shared `EmitViewBody` helper. Body SELECT routes through `EmitFragmentDefault` so the `SelectStatement` override fires (CTE prelude + clause-keyword right alignment).
  - **4d-iii**: CREATE / ALTER FUNCTION (scalar / inline TVF / multi-statement TVF)
  - **4d-iv**: CREATE / ALTER TRIGGER
  - **4d-v**: CREATE / ALTER TABLE (column defs, constraints, indexes — different shape from the others)
- **4e**: Body-block and control-flow statements.
  - ~~**4e-i**~~: Landed 2026-04-25. `BeginEndBlockStatement` (with `BeginEndAtomicBlockStatement` fallback guard), `TryCatchStatement`. Bundled with 4d-i.
  - ~~**4e-ii**~~: Landed 2026-04-25. `IfStatement`, `WhileStatement` via shared `EmitConditionalBody` + `EmitConditionalPredicate` helpers. Bundled fix: AtLineStart guard before `)` in `ScalarSubquery` / `InPredicate` / `ExistsPredicate` (latent 4b-ii bug — surfaced when those overrides got called outside a parent clause scope, leaving a blank line before the closing `)`). New Known-limitation entry: ELSE IF stairstep.
  - ~~**4e-ii-b**~~: Landed 2026-04-25. Vertical spacing rule for body recursion. Shared `EmitBodyStatements` helper used by `EmitProcedureBody` / `BeginEndBlockStatement` / `EmitTryCatchHalf`. Rule: blank line between siblings iff at least one is "block-level" (multi-line in our output: SELECT/INSERT/UPDATE/DELETE/MERGE/IF/WHILE/BEGIN-END/TRY-CATCH). Single-liners (DECLARE/SET/RETURN/transaction-control/RAISERROR) stay tight as a group; block statements get breathing room on both sides.
  - **4e-iii**: `DeclareStatement`, `SetStatement`, `PredicateSetStatement`, `ReturnStatement`, transaction control. Generator-fallback emits these correctly today; this slice is taste refinements only (parameter formatting overlaps with 4f).
- **4f**: PIVOT / APPLY / ParenthesisExpression / remaining expression types
- **4g**: Comment attachment rules (populates the dict; emission hooks become real)

### 4a default statement emission — *temporary scaffold*

For any fragment without an overridden `ExplicitVisit`, `TSqlFormatterVisitor.EmitFragmentDefault`
pipes the fragment through a `Sql170ScriptGenerator` instance configured from the current
`FormatterOptions`. This is the 4a placeholder — Path A-style default output, good enough for
"no regex-splitter pathologies" but not the final aesthetic.

**Containment rule.** `EmitFragmentDefault` (and the `Sql170ScriptGenerator` field that backs it)
is 4a-only scaffolding. Every 4b–4f visitor override replaces one fragment type's generator
fallback with custom emission. Once 4g lands and the fragment coverage is complete,
`EmitFragmentDefault` and the generator field must be deleted entirely. If they still exist
after 4g merges, that's a bug — the 4a scaffold quietly became permanent infrastructure, and
the Path B architecture is no longer pure. The method in `TSqlFormatterVisitor.cs` carries a
comment flagging this.

The generator's known comment loss does not manifest in 4a because `CommentEmission` is a no-op
in this step. 4g replaces the generator-emit path with fragment-specific emission that routes
comments through the attachments map.

### Clause-keyword right alignment (added in 4a)

Decision made during 4a (after image-3 vs image-2 comparison in the editor): clause keywords
within a statement right-align so the rightmost character of every keyword lands in the same
column. `SELECT` / `FROM` / `WHERE` pad the shorter ones with leading spaces; continuations
(`AND`, `OR`) use `WriteClauseKeyword` too and therefore also right-align. Example with
`maxKw = 6` (`SELECT`):

```
SELECT *
  FROM t_employee
 WHERE 1 = 1
   AND 2 = 2
```

Rationale: the clean left edge on clause bodies is easier to scan vertically than left-aligned
keywords where the body column jumps by keyword length. Image 3 in the 4a session showed the
style paired with collapsed subqueries — that's not what this decision is; subqueries are still
expected to break to indented blocks in 4b, at which point each subquery opens its own scope
with its own local `maxKw`.

**Emitter API** (internal, lives on `SqlEmitter`):

- `IDisposable BeginClauseScope()` — opens a scope; nested scopes are stack-based (4b-ii).
- `void WriteClauseKeyword(string keyword)` — records a keyword for the current line. Outside a scope, falls back to `WriteKeyword`.
- Plain `Write` / `NewLine` inside a scope append to the current line's body / end the current line.
- On dispose: flush with `maxKw = max(len(keyword) over lines)`. Keyworded lines: left-pad by `maxKw − keyword.Length` spaces, then the (case-cased) keyword, then a single space, then body. Body-only continuation lines: indent to column `maxKw + 1`. `AlignClauseBodies = false` disables padding entirely (left-aligned fallback).
- Scope captures the outer `IndentLevel` at `BeginClauseScope`; the outer indent prefix is prepended to every flushed line.

**Nested scopes (4b-ii — stack-based).** `BeginClauseScope` may now be called while another
scope is active. Internally the emitter holds a `Stack<ClauseBuffer>`; each call pushes,
each dispose pops. On pop, if the stack empties, the buffer flushes to `_sb` directly. If the
stack is non-empty after pop, the popped buffer is rendered to a string (via its own
`FlushTo`) and the rendered lines inject into the new top buffer's body — first line
continues the parent's current `LineEntry`, each subsequent line becomes a body-only
continuation. The parent's eventual flush prepends `parentOuterIndent + (maxKw_parent + 1)`
to each continuation line, so the inner block visually nests inside the parent's body
column. This preserves the single-canonical-flush-path invariant (every line in `_sb` goes
through one `FlushTo` call somewhere in the stack).

**Strip-parent-outer on nested pop (4c step 0).** Before 4c, each nested pop left the inner
buffer's full `captured * IndentSize` leading spaces on each injected line, on top of which
the parent's flush added its own `outerIndent + (maxKw + 1)`. The cumulative math was fine
when the outermost scope had `captured = 0` (top-level queries), but in a CTE's inner query
(outermost scope `captured = 1`) every further nesting level picked up an extra `IndentSize`
of leading spaces — e.g. `CTE → inner QuerySpec → LEFT JOIN (SELECT …)` placed the
derived table's inner SELECT at col 19 instead of col 15. Fix: when popping into a parent,
strip `parent.CapturedIndentLevel * IndentSize` leading spaces from each injected line. The
stripped portion is exactly the parent-outer contribution the parent will re-add during its
own flush, so the final column is `parent.outerIndent + (parent.maxKw + 1) + (inner line's
leading past parent.outerIndent)` — `IndentSize` past the parent's body col per level, not
cumulative. Top-level `Format_WhereInSubquery` shape unchanged (strip = 0 when parent
captured = 0); `Format_DoublyNestedSubquery` and `ClauseScope_DoublyNestedRendersEachLevel`
asserts updated to reflect the new, consistent model.

**Not wired into the visitor in 4a.** The 4a visitor overrides only `TSqlScript` / `TSqlBatch`;
statement bodies go through `EmitFragmentDefault` (generator) and never call `WriteClauseKeyword`.
The alignment feature is exercised only by `SqlEmitterTests` in 4a. Every 4b clause-level
override (`SelectStatement`, `FromClause`, `WhereClause`, `GroupByClause`, `OrderByClause`,
`HavingClause`, `QueryExpression` joins) will wrap its emission in `BeginClauseScope` and use
`WriteClauseKeyword` instead of `WriteKeyword`.

## Parse strategy

- **Primary parser**: `TSql170Parser(initialQuotedIdentifiers: true)`.
- **Fallback staircase** (in `SelectionParseStaircase.TryParse`): `Parse` → `ParseStatementList` → `ParseExpression`. Returns on first success. `4A-PLAN.md` calls the middle rung `ParseStatement`; ScriptDom's real API name is `ParseStatementList` — same intent.
- **All-fail route**: `ScriptDomFormatter.Format` returns `LegacyHogimnFormatter.Format(sql)`.
- **Hard timeout**: 2 seconds via `Task.Run` + `Task.Wait`. On timeout, returns input unchanged (a Hogimn fallback on a parse-hung input would be just as slow, so we don't waste the user's time).
- **Visitor throw**: caught at the top level in `ScriptDomFormatter.Format`; logs via `AppLogger.LogError` and falls back to Hogimn. The dispatcher (`SqlFormatterService`) raises `FallbackOccurred` when the toggle gate allows — `ScriptDomFormatter` does not duplicate that.

## Fragment handling log

Append an entry per fragment type as 4b–4g implement them. One paragraph each: what the fragment is, how we emit it, what taste decisions were made.

### TSqlScript (4a)
Iterates `Batches`; emits `\nGO\n` separator between batches (not before first, not after last). No formatting of batch contents beyond delegating to `ExplicitVisit(TSqlBatch)`.

### TSqlBatch (4a)
Iterates `Statements`; calls `CommentEmission.EmitLeadingCommentsFor` and `EmitTrailingCommentsFor` at each statement boundary (both no-op in 4a); emits each statement through `EmitFragmentDefault`; blank line between statements.

### SelectStatement (4b-i)
Emits `WithCtesAndXmlNamespaces` (if present) via the 4a generator scaffold followed by a newline — full CTE formatting lands in 4b-iii. Dispatches `QueryExpression`: if `QuerySpecification`, calls its override (runs our aligned-clause emission); else falls back to `EmitFragmentDefault`. Niche `SelectStatement` members (`Into`, `On`, `ComputeClauses`, `OptimizerHints`) trigger a whole-statement fallback to preserve correctness until their dedicated handling arrives.

### QuerySpecification (4b-i; list-wrap 4b-iii-b)
Opens a `ClauseScope`, then emits clauses in canonical order, each via `WriteClauseKeyword` plus a body written directly through the emitter:

- `SELECT` — `UniqueRowFilter` (`DISTINCT` / `ALL`) prefix as needed; `SelectElements` dispatched through `EmitSelectElementBody` (subquery-in-SELECT breaks to block; otherwise generator scaffold) inside `EmitWrappedList`.
- `FROM` — `TableReferences` dispatched through `EmitTableReferenceBody` (JOINs / derived tables / CROSS|OUTER APPLY via their own overrides; other shapes single-line generator) inside `EmitWrappedList`. The wrap path handles old-style implicit joins (`FROM a, b, c`); modern JOIN trees are a single outer TableReference so the helper short-circuits.
- `WHERE` / `HAVING` — `EmitSearchConditionBody` dispatches `BooleanBinaryExpression` (AND / OR), `InPredicate(subquery)`, `ExistsPredicate`, and `BooleanComparisonExpression` with a multi-line side (`ScalarSubquery` or `CaseExpression`) through the visitor. Default case (the remaining single-line predicates: non-subquery comparisons, `LikePredicate`, `BetweenExpression`, etc.) renders inline via generator.
- `GROUP BY` — per-`GroupingSpecification` generator scaffold inside `EmitWrappedList`.
- `ORDER BY` — per-`ExpressionWithSortOrder` generator scaffold inside `EmitWrappedList`. (`ORDER BY` lives on `QueryExpression`, inherited into `QuerySpecification`.)

`EmitWrappedList` wraps at `MaxLineLength * 2/3` (= 80 at default 120), measured against the sum of per-element first-line renders plus `", "` separators. Multi-line element renders (e.g. subqueries in SELECT) contribute only their first-line length — the element's own break-to-block handles its vertical layout.

`TopRowFilter`, `OffsetClause`, and `ForClause` still trip `QuerySpecRequiresFallback` at the SelectStatement level (or route through `EmitFragmentDefault` if reached inside a subquery), to be replaced by their own future slices.

### ScalarSubquery / InPredicate (with subquery) / ExistsPredicate (4b-ii)
Each emits opening `(`, NewLine, `Indent()`, recurses via `subquery.QueryExpression.Accept(this)`, NewLine, dedent, closing `)`. The accept call dispatches to `ExplicitVisit(QuerySpecification)` which opens its own `BeginClauseScope` — so these three overrides do **not** open a scope themselves (doing so would double-wrap and double-indent). For non-`QuerySpecification` subquery shapes (`BinaryQueryExpression` — UNION etc.), `EmitSubqueryQueryExpression` falls through to `EmitFragmentDefault`; proper handling lands in 4b-iv.

`InPredicate.Subquery == null` (values list) falls through to generator. `BooleanComparisonExpression` where one side is a `ScalarSubquery` is handled by `EmitSearchConditionBody`'s top-level dispatcher — the non-subquery side renders inline via generator scaffold (`EmitExpressionScaffold`), the subquery side dispatches through the visitor. AND/OR-mixed search conditions (`BooleanBinaryExpression` containing a subquery) are **not** dispatched — they fall through to generator, so a subquery under AND/OR renders inline. See § Known limitations.

### QuerySpecification fallback at SelectStatement level (4b-ii bug fix)
`Sql170ScriptGenerator.GenerateScript(QuerySpecification)` on a *bare* QuerySpec with JOINs silently drops trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY) — a ScriptDom quirk where bare-QuerySpec generation isn't a fully-supported entry point. To dodge this, `ExplicitVisit(SelectStatement)` checks `QuerySpecRequiresFallback(qs)` before dispatching to the QuerySpec override; when true, it emits the *whole* SelectStatement via `EmitGeneratorRaw` (the surrounding-SelectStatement generator path emits all clauses). 4b-iii-a removed `hasJoins` and `hasMultiTableFromClause` from the trip list (both now handled in-visitor); remaining trip-flags (`TopRowFilter` / `OffsetClause` / `ForClause`) keep the fallback alive until their own slices land.

`EmitGeneratorRaw` is a small helper that does the same generator-emit work as `EmitFragmentDefault`'s tail but skips its routing branches — needed when an override falls back to generator for its *own* fragment type (calling `EmitFragmentDefault` would re-enter the override and infinite-loop).

### QualifiedJoin / UnqualifiedJoin / QueryDerivedTable (4b-iii-a)

Each JOIN renders as a body-continuation line in the outer FROM scope (no per-JOIN `BeginClauseScope` — the outer QuerySpec's body column is the base indent). `QualifiedJoin.FirstTableReference` recurses through `EmitTableReferenceBody` so JOIN chains (`a JOIN b JOIN c`) stack left-associatively. `SecondTableReference` renders inline after the JOIN keyword phrase.

**ON layout.** Inline by default: `INNER JOIN u ON cond`. When the rendered condition text exceeds `MaxLineLength * 2/3` (a heuristic — a precise check needs the flush-time outer body column, which depends on the not-yet-finalised `maxKw`), the override emits `NewLine + <IndentSize spaces>ON <cond>` — so ON lands at outer-body-col + `IndentSize`, visually nested under the JOIN keyword. Overflow test uses `MaxLineLength=120` default → threshold = 80.

`UnqualifiedJoin` handles CROSS JOIN, CROSS APPLY, and OUTER APPLY — all three are `UnqualifiedJoinType` enum values on the same ScriptDom type, so no separate `CrossApplyTableReference` / `OuterApplyTableReference` overrides were needed (plan anticipated separates; actual ScriptDom shape collapsed them).

`QueryDerivedTable` is the subquery-in-FROM case. Same break-to-block pattern as `ScalarSubquery` (4b-ii): `(` + NewLine + `Indent()` + recurse on inner `QueryExpression` + NewLine + `)` + ` AS alias`. The inner QuerySpec opens its own `BeginClauseScope`, so we do not.

**Old-style implicit joins** (`FROM a, b, c`) are supported via a comma-joined body in the FROM-clause emission loop. Rare in modern code but trivial to handle since FROM body already iterates per `TableReference`.

### WithCtesAndXmlNamespaces / CommonTableExpression (4b-iii-b)

CTEs emit at the current (statement-level) indent — no own clause scope. `WITH ` starts at col 0, each CTE dispatched via `Accept`. Chained CTEs separated by `","` + NewLine (trailing-comma per D3); the `,` lands on the previous CTE's closing `)` line. The closing `)` of each CTE also sits at col 0.

Each CTE body indents by `IndentSize` (= 4) via a single `Indent()` around `EmitSubqueryQueryExpression(cte.QueryExpression)`. The inner `QuerySpecification.Accept` opens its own `BeginClauseScope` with `_capturedIndentLevel = 1`, so inner `SELECT` / `FROM` / `WHERE` keywords right-align locally with their own `maxKw`, visually nesting inside the CTE's `(...)`.

The CTE override uses `_emitter.AtLineStart` to skip a redundant NewLine before the closing `)`. When the CTE's inner clause scope pops at top-level (no parent scope), `EndClauseScope` flushes the buffer directly to `_sb` without `TrimEnd`-ing the trailing newline — so the inner's own trailing NL already positions the cursor for `)`. In contrast, `ScalarSubquery` / `QueryDerivedTable` (which always pop into a parent scope, where the injection path does `TrimEnd`) unconditionally NewLine before their closing `)`.

Optional `CommonTableExpression.Columns` (the `(col1, col2)` after the CTE name) renders via per-identifier generator scaffold, comma-joined inline. Promotion to `EmitWrappedList` deferred until corpus shows it matters.

Smoke of the 3-deep `scripts/formatter-test-corpus/02-nested-cte.sql` shows clean stacking: each CTE body at col 4, inner query's own right-aligned clauses inside that (e.g. DepartmentTotals's GROUP BY at col 4, its SELECT padded to col 6, FROM padded to col 8 — local `maxKw = 8` for GROUP BY).

### InsertStatement (4c)
No outer clause scope (D3). Emits `INSERT [INTO|OVER] <target>` on line 1; optional column
list at +`IndentSize` on line 2 (simple inline comma-join — list wrap for long column lists
is 4d scope); shared `EmitOutputClause` dispatcher (grammatically sits before the source);
then source via `EmitInsertSource` — dispatches `ValuesInsertSource`, `SelectInsertSource`,
`ExecuteInsertSource`. For VALUES: single short row inline (`VALUES (…)`), single long row
or multi-row wraps to one-per-line at +`IndentSize` with trailing commas (D3 / D10). For
SELECT: routed through `EmitSubqueryQueryExpression` (handles `QuerySpecification` and
`BinaryQueryExpression` — UNION source stacks correctly). `TopRowFilter` trips whole-
statement fallback (D6). Trailing `;` not emitted (consistent with SELECT).

### UpdateStatement (4c)
UPDATE / SET / OUTPUT / FROM / WHERE right-align in one clause scope (D1). `maxKw = 6`
(UPDATE / OUTPUT). FROM body reuses `EmitTableReferenceBody` / `EmitWrappedList` — JOINs
stack identically to a SELECT's FROM. SET assignments wrap via `EmitWrappedList` + per-
`SetClause` generator scaffold (subquery-in-NewValue is out of scope for 4c; 4f picks up
scalar-expression overrides that would break the subquery to block inside a SET). WHERE
reuses `EmitSearchConditionBody` — subquery-bearing search conditions break to block the
same way they do in a SELECT's WHERE. `TopRowFilter` trips fallback.

### DeleteStatement (4c)
Two shapes (D2). Simple form (`FromClause == null`): `DELETE` is the keyword, `FROM
<target>` is the body — keeps scope `maxKw` at 6, so WHERE pads by 1 (col 1). Extended
form (`FromClause != null`, typically with JOINs): `DELETE <target>` and `FROM <join-tree>`
are separate keyword lines inside the scope, right-aligning with WHERE / OUTPUT. Grammar
order: DELETE / (target) → OUTPUT → FROM → WHERE. `TopRowFilter` trips fallback.

### MergeStatement (4c)
Header clause scope (D4) aligns `MERGE INTO` / `USING` / `ON`, `maxKw = 10` ("MERGE INTO").
USING source renders via `EmitTableReferenceBody` — typically a `QueryDerivedTable` inside
parens; since the inner QuerySpec opens its own clause scope at +`IndentSize` past the
header's captured indent, 4c step 0's strip-parent-outer fix applies here too (inner SELECT
lands at header body col + IndentSize, not cumulatively further right). ON body routes
through `EmitSearchConditionBody`, so a multi-AND merge condition right-aligns inside the
header scope.

WHEN clauses emit at statement indent outside the header scope. Each clause: `WHEN
[NOT] MATCHED [BY TARGET|SOURCE] [AND <cond>] THEN\n<action at +IndentSize>`. The AND
condition renders inline via generator scaffold (single-line); multi-line AND in a WHEN
filter rides the existing Known limitation (AND/OR outside clause scope). `EmitMergeAction`
dispatches `UpdateMergeAction` (emits `UPDATE SET <wrapped-list>`), `DeleteMergeAction`
(`DELETE`), and `InsertMergeAction` (`INSERT (cols)\n<source via EmitInsertSource>`).

OUTPUT (when present) emits after all WHENs. Terminating `;` always emitted when
`IncludeSemicolons = true` — grammatically required for MERGE re-parse. Retires the three
`04-merge.sql` / `pathA.sql` / `original.sql` harness REGRESSION parse failures; 48/0/5
→ 51/0/2 (remaining 2 are the hogimn-baseline parse failures carried over since 4a).

### CreateViewStatement / AlterViewStatement / CreateOrAlterViewStatement (4d-ii)

Three thin overrides delegate to `EmitViewBody(ViewStatementBody, keywordPrefix)`. Header
shape: `CREATE [OR ALTER]/ALTER VIEW <name>` + optional inline `(col1, col2)` column list
(per-identifier generator scaffold, no wrap — mirrors `CommonTableExpression.Columns`;
promotion to `EmitWrappedList` deferred jointly with CTE columns until corpus surfaces a
long view column list) + optional `WITH <opts>` line (`ENCRYPTION` / `SCHEMABINDING` /
`VIEW_METADATA`, generator-rendered per option, comma-joined) + `AS` + body `SelectStatement`
recursed via `EmitFragmentDefault` (so the `SelectStatement` override fires, picking up CTE
prelude + clause-keyword right-alignment) + optional `WITH CHECK OPTION` trailer at col 0
(grammar: before terminating `;`). Body indent is flat (no `Indent()` around the body
SELECT) — matches SSMS / GittyExport convention for view scripting.

`ViewStatementBody.IsMaterialized` (Synapse materialized views — extra DDL not modeled)
trips an `EmitGeneratorRaw` fallback at the top of `EmitViewBody`. Defensive guard, same
pattern as `BeginEndAtomicBlockStatement` falling out of `BeginEndBlockStatement`'s
override.

No trailing `;` is emitted — the body `SelectStatement` doesn't emit one, and views don't
require one for parse. `WITH CHECK OPTION` lands on its own line after the body.

### Known AND/OR byproduct in ON (4b-iii-a, carried into 4b-iv)
`Sql170ScriptGenerator` renders `BooleanBinaryExpression` (AND / OR) as multi-line by default. Our `EmitBooleanScaffold`-style inline scaffold for ON writes the rendered text as a raw string, so embedded newlines from the generator land in the body without getting the scope's body-column prefix applied to each sub-line — continuation lines sit at column 1 instead of at the WHERE/ON body column. Same root cause as the WHERE-with-AND visual glitch seen in the staffing-report smoke. Fixed when 4b-iv takes over `BooleanBinaryExpression` rendering; the long-ON test avoids it by using a simple single comparison.

## Known limitations

- **AND/OR inside a non-clause-scope context (ON, CASE WHEN condition, arbitrary scalar position)** — `BooleanBinaryExpression` falls back to `EmitGeneratorRaw` outside a clause scope (D3 in 4b-iv), so a multi-AND `JOIN … ON a AND b AND c` still renders with the generator's embedded-newline-at-col-1 quirk. WHERE / HAVING (which always run inside their clause scope, including inside subqueries) are clean. The fix is a separate slice — needs a redesign of ON's body model so it has a clause-scope-like alignment target.
- **`ELSE IF` chains render as an indent stairstep, not flattened to one line per branch** *(introduced 4e-ii, will be a corpus issue)*. T-SQL `IF a ELSE IF b ELSE c` parses with the second `IF` as `ElseStatement`; our `IfStatement` override recurses naturally, so each nested IF lands one indent level deeper. Real-world T-SQL uses `ELSE IF` chains constantly — this will be one of the first things that looks ugly on corpus sprocs. Concrete shape locked in `Tests/ConditionalStatementTests.cs::Format_ElseIf_StairstepLocked` for visibility.
  ```
  IF @x = 1
      SELECT 1;
  ELSE
      IF @x = 2          <-- stairstep starts here
          SELECT 2;
      ELSE
          SELECT 3;
  ```
  **Fix sketch (~10 lines)**: in `ExplicitVisit(IfStatement)`, after emitting the THEN body, detect `stmt.ElseStatement is IfStatement nested && nested.ThenStatement != null` and emit `ELSE IF <nested.Predicate>` on one line at the current indent, then recurse into `nested.ThenStatement` / `nested.ElseStatement` instead of dispatching the whole nested IF. Capture-test pattern: extend `Format_ElseIf_StairstepLocked` (rename to `Format_ElseIf_FlattensToSingleLine`), update expected output, lock. Watch out for: nested IF that has its own ELSE — recurse normally on the second-level ELSE.

## Known deviations from FORMATTER-OVERHAUL.md

- **IncludeSemicolons default flipped to `true`** — `FORMATTER-OVERHAUL.md` § "Batch Preservation" says false-to-match-Hogimn. Spike evidence and SQL Server semantics drove the flip. See D2 in `docs/4A-PLAN.md`.
- **Path B committed, Path A eliminated** — `FORMATTER-OVERHAUL.md` step 3's "Path A + overrides" branch is dead; `FORMATTER-SPIKE.md` records the evaluation showing 100% comment loss under `Sql170ScriptGenerator`. All of 4a–4g executes as the Path B column of the doc.
- **Middle parse rung named `ParseStatementList`, not `ParseStatement`** — ScriptDom API discrepancy; same semantics.
- **Clause-keyword right alignment added as a `FormatterOptions` field** — not in `FORMATTER-OVERHAUL.md`. Decision reached during 4a after comparing Hogimn vs generator output in the editor. See "Clause-keyword right alignment" above.
- **4a visitor routes unoverridden fragments through `Sql170ScriptGenerator`** — the plan's literal `statement.Accept(this)` would emit nothing for statement bodies in 4a (no statement-level `ExplicitVisit` overrides exist yet). The pragmatic deviation keeps the regression harness at a non-trivial baseline (48/53) and gives the editor visible output for manual testing. See the containment rule above — this is 4a scaffold only and must be removed by 4g.

## Progress log

### 4a — 2026-04-24 (closed)
- Visitor skeleton landed. `ExplicitVisit(TSqlScript)` and `ExplicitVisit(TSqlBatch)` only.
- Four TBD `FormatterOptions` filled; `IncludeSemicolons` flipped to `true`.
- `AlignClauseBodies = true` added; `SqlEmitter` gained `BeginClauseScope` / `WriteClauseKeyword` with buffer-and-flush right-alignment. 7 emitter tests. 4a visitor doesn't call the new API yet (generator scaffold handles statement bodies); 4b clause overrides will wire it up.
- 2-second parse timeout wired in `ScriptDomFormatter`.
- `CommentAttacher` / `CommentEmission` are no-op hook points (4g).
- Selection parse staircase lands.
- `TSqlFormatterVisitor.EmitFragmentDefault` routes unoverridden fragments through `Sql170ScriptGenerator` as 4a scaffold. Marked for deletion after 4g (see containment rule above).
- Regression harness (53 files, incl. 11-file corpus + spike subfolders): **48 canonical matches, 0 canonical mismatches, 5 parse failures** (3 `REGRESSION` on MERGE-variant inputs — `Sql170ScriptGenerator` collapses MERGE output such that the round-tripped text no longer re-parses; 2 baseline failures on `reports/spike/hogimn/*.sql` where the pre-captured Hogimn output already didn't parse). The 3 regressions are inherent to 4a's generator-based default emission and will disappear when 4c adds a dedicated `MergeStatement` override.

### 4b-i — 2026-04-24
- `SelectStatement` and `QuerySpecification` overrides landed. Outer `SELECT` / `FROM` / `WHERE` / `GROUP BY` / `HAVING` / `ORDER BY` now right-align in the editor on queries whose `QuerySpecification` doesn't trip the niche-feature fallbacks (`TOP`, `OFFSET`, `FOR`, multi-table `FROM`, any `JoinTableReference`).
- `Services/Formatting/Visitor/ClauseBodyEmitter.cs` added — internal helper that feeds generator body text into the active clause scope (split-on-`\n`, strip-leading-ws-per-line, emit as `Write` + `NewLine` so scope continuation-indent applies).
- `EmitFragmentDefault` now routes `SelectStatement` through its override; each subsequent slice adds another routing branch. After 4g the whole method (and the generator field) disappears.
- Tests: 7 new facts in `Tests/QuerySpecificationFormattingTests.cs`, all pass. Full suite 45/45.
- Regression harness: **48 / 0 / 5** — identical to the 4a baseline (canonical comparison is AST-equivalent, so our alignment changes don't shift the count). The 3 MERGE-variant regressions still exist; 4c kills them.
- Screenshot-query output (captured from the real formatter for the 4b-ii starting shape):
  ```
  SELECT *
    FROM [dbo].[Employees]
   WHERE id IN (SELECT *
         FROM   [dbo].[Employees])
  ```
  Outer aligned. Inner subquery still inline (generator output inside the WHERE body) — **4b-ii** replaces this with a break-to-indented-block per D9.

### 4b-ii — 2026-04-25
- `SqlEmitter` clause-buffer model converted from a single `_activeClause` field to a `Stack<ClauseBuffer>`. `BeginClauseScope` no longer throws on nested calls; on inner-pop, the popped buffer is rendered to a string and injected into the parent's body (first line continues the parent's current `LineEntry`, subsequent lines become body-only continuations). The single-canonical-flush-path invariant is preserved.
- Three new visitor overrides — `ExplicitVisit(ScalarSubquery)`, `ExplicitVisit(InPredicate)`, `ExplicitVisit(ExistsPredicate)`. Each emits opening `(` + NewLine + `Indent()` + recurse + NewLine + dedent + `)`. They do not open their own `BeginClauseScope` — `QueryExpression.Accept(this)` dispatches to `ExplicitVisit(QuerySpecification)` which already opens one (opening one in both places double-wraps).
- Top-level dispatchers `EmitSearchConditionBody` (WHERE / HAVING) and `EmitSelectElementBody` (SELECT elements) detect the three subquery-bearing shapes and route through the visitor; everything else still flows through the generator. AND/OR-mixed search conditions are out of scope (see § Known limitations).
- 4b-i latent bug fix: `Sql170ScriptGenerator.GenerateScript(QuerySpecification)` on a bare QuerySpec with JOINs silently drops trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY). Surfaced when 4b-ii reran the harness — manifested as 8 canonical mismatches in the existing JOIN-bail path. Fixed by adding a SelectStatement-level fallback that fires before the QuerySpec dispatch and emits the whole SelectStatement via `EmitGeneratorRaw` (surrounding-SelectStatement generator path emits all clauses correctly). Required factoring `EmitGeneratorRaw` out of `EmitFragmentDefault` so the SelectStatement override could fall back without re-entering itself and stack-overflowing.
- Tests: `ClauseScope_NestedScopeThrows_4bWillRelaxThis` deleted from `SqlEmitterTests.cs`; three new emitter-level nested-scope tests added (`NestedInnerInjectsIntoParentBody`, `NestedHasOwnLocalMaxKw`, `DoublyNestedRendersEachLevelIndependently`). New file `Tests/SubqueryFormattingTests.cs` with 5 facts covering the spec test cases (WHERE IN subquery, WHERE EXISTS, ScalarSubquery in SELECT, ScalarSubquery in WHERE, doubly-nested) — all exact-equals against the natural code output. Full suite: **52 / 52** (was 45 in 4b-i; +3 emitter nested + 5 subquery − 1 deleted throw = 52).
- Regression harness: **48 / 0 / 5** — restored to 4b-i baseline after the latent-bug fix above. Same 5 parse failures (3 MERGE-variant — 4c kills them; 2 spike-data baselines).
- Screenshot-query output (captured for the 4b-iii starting shape, **confirmed visually in the running app**):
  ```
  SELECT *
    FROM [dbo].[Employees]
   WHERE id IN (
             SELECT *
               FROM [dbo].[Employees]
         )
  ```
  Inner `SELECT` at col 11, inner `FROM` at col 13, closing `)` at col 7 (outer body column). Inner has its own local `maxKw=6` (`SELECT`); inner `FROM` pad is 2 (= 6−4). Note: D9's hand-drawn example in `4B-PLAN.md` showed slightly different column counts (~10/12/9 by my count) — per agreed convention, the test asserts what the code actually produces and D9's example is illustrative.
- **End-of-phase verification**: full test suite green (52/52), regression harness at 4b-i baseline (48/0/5), app launches and the screenshot query renders as above in the editor. 4b-ii ships clean. 4b-iii (joins, column lists, CTEs, derived tables) is the next slice and picks up the inner-SELECT-with-JOINs case (currently routes through the bare-QuerySpec generator and drops trailing clauses — see § Known limitations).

### 4b-iii-a — 2026-04-25
- **Plan split**: `docs/4B-PLAN.md` § 4b-iii was too large for one session; 4b-iii split into a (this slice: FROM body — joins + derived tables) and b (list wrap + CTEs, next session). Plan file: `~/.claude/plans/4b-iii-a-joins-derived-tables.md`.
- Three new visitor overrides: `ExplicitVisit(QualifiedJoin)`, `ExplicitVisit(UnqualifiedJoin)`, `ExplicitVisit(QueryDerivedTable)`. Plan anticipated five (separate `CrossApplyTableReference` / `OuterApplyTableReference`) but CROSS APPLY / OUTER APPLY are `UnqualifiedJoinType` enum values on the same `UnqualifiedJoin` type — collapsed into the one override.
- `EmitTableReferenceBody` dispatcher added as the single routing point for FROM-body TableReferences. Unhandled subtypes fall through to generator on the subfragment (safe — the bare-QuerySpec trailing-clause drop is specific to `GenerateScript(QuerySpecification)`, not to arbitrary TableReference generation).
- `QuerySpecRequiresFallback` trimmed: `hasJoins` and `hasMultiTableFromClause` removed. Remaining trip-flags (`TopRowFilter` / `OffsetClause` / `ForClause`) stay until their own future slices. Known limitation #3 from 4b-ii (subquery-with-JOINs trailing-clause drop) is dead — regression test `Format_JoinInsideSubquery_PreservesTrailingClauses` locks it.
- Old-style implicit joins (`FROM a, b, c`) now render correctly via comma-joined body loop.
- **Long ON** breaks to own line at `IndentSize` pad under JOIN when rendered condition > `MaxLineLength * 2/3` (heuristic). Plan's D-shape example used 3-space pad; actual implementation uses `IndentSize` (4 default). Test assertions match actual output per capture-then-update convention.
- Tests: 7 new facts in `Tests/JoinAndCteFormattingTests.cs`, all pass. Full suite **59/59** (52 + 7).
- Regression harness: **48 / 0 / 5** — baseline preserved. Same 5 parse failures (3 MERGE-variant — 4c; 2 spike-data baselines).
- Smoke-tested real SELECT body from `07-real-sproc-staffing-report.sql` (CREATE PROCEDURE itself is 4d-fallback). JOIN stacking renders cleanly; column list doesn't wrap (4b-iii-b's job); WHERE's `OR` shows the AND/OR visual glitch described under § Known limitations — 4b-iv fix.
- **End-of-phase verification**: 59/59 tests green, harness 48/0/5, app launch pending. 4b-iii-b (list wrap + CTEs) is the next slice.

### 4b-iii-b — 2026-04-25
- List wrap added for `SELECT` / `GROUP BY` / `ORDER BY` / old-style `FROM` via a shared private helper `EmitWrappedList<T>` on `TSqlFormatterVisitor`. Threshold = `MaxLineLength * 2/3` (= 80 at default 120) measured against per-element first-line-only rendered lengths + `", "` separators. Multi-line-rendering elements (subqueries) contribute only their first-line length so the list decision doesn't force-wrap when an element handles its own break-to-block internally.
- CTE overrides added: `ExplicitVisit(CommonTableExpression)` and `ExplicitVisit(WithCtesAndXmlNamespaces)`. Flat-indent shape per D2 (discussed in plan: option (b)'s per-level cost was ~9 cols via WITH's own clause scope; option (a)'s flat indent costs +4 cols per nesting level — chosen for the 3–4-deep CTE chains in WMS sprocs).
- `SelectStatement` now dispatches `WithCtesAndXmlNamespaces` via `Accept(this)` instead of `EmitFragmentDefault` so the new override fires.
- New public property `SqlEmitter.AtLineStart` — true when the cursor is at the start of a fresh line and no clause scope is active. Used by `ExplicitVisit(CommonTableExpression)` to skip a redundant NewLine before the closing `)` when the inner QuerySpec's clause scope flushes straight to `_sb` (no parent to `TrimEnd` into, unlike ScalarSubquery's path).
- `ClauseBodyEmitter.cs` deleted. Four call sites replaced:
  - GROUP BY body — now `EmitWrappedList` over `EmitGroupingSpecificationBody`.
  - ORDER BY body — now `EmitWrappedList` over `EmitOrderByElementBody`.
  - `EmitTableReferenceBody`'s fallback for non-JOIN / non-derived-table TableReferences — now inline single-line `_generator.GenerateScript + Write`.
  - `EmitSearchConditionBody`'s default (non-subquery-bearing) case — now a private `EmitMultiLineGeneratorBody(TSqlFragment)` method (~6 lines, same trim-and-emit logic the helper had). Marked for deletion when 4b-iv's `BooleanBinaryExpression` override retires the last generator-fallback path in search conditions.
- Tests: 7 new facts in `Tests/JoinAndCteFormattingTests.cs` (`Format_ShortSelectList_StaysSingleLine`, `Format_LongSelectList_Wraps`, `Format_LongGroupBy_Wraps`, `Format_LongOrderBy_Wraps`, `Format_LongOldStyleFromList_Wraps`, `Format_SingleCte_FormatsCleanly`, `Format_ChainedCtes_StackVertically`). Full suite **66/66** (was 59).
- Regression harness: **48 / 0 / 5** — same as prior baselines. No canonical-match movement; CTE-using corpus file (`02-nested-cte.sql`) was already canonical-matching via the pre-4b-iii-b generator scaffold, so the output shape change is AST-equivalent.
- Smoke-tested `02-nested-cte.sql` (3-deep chain with DepartmentTotals / RankedDepts / TopDepts + a final JOINed SELECT). Output captured in § Fragment handling log entry for `WithCtesAndXmlNamespaces / CommonTableExpression`. One observation for 4b-iv or later: when the outer SELECT of a CTE chain has `ORDER BY` as its widest keyword (wider than `SELECT` / `FROM`), it sits at col 0 alongside the CTE `WITH`, which is correct per the D7 right-alignment rule but visually surprising. Not a bug; flagging for future taste review.
- **End-of-phase verification pending**: 66/66 tests green, harness 48/0/5, app launch pending. 4b-iv (CASE + UNION + `BooleanBinaryExpression`) is the next slice.

### 4b-iv — 2026-04-25
- Four new visitor overrides: `ExplicitVisit(SearchedCaseExpression)`, `ExplicitVisit(SimpleCaseExpression)`, `ExplicitVisit(BinaryQueryExpression)`, `ExplicitVisit(BooleanBinaryExpression)`. Plus dispatcher updates: `EmitSelectElementBody` / `EmitComparisonOperandScaffold` / `EmitExpressionScaffold` route `CaseExpression`; `EmitSubqueryQueryExpression` routes `BinaryQueryExpression`; `EmitSearchConditionBody` routes `BooleanBinaryExpression`. `EmitFragmentDefault` got the four new branches.
- New emitter property `SqlEmitter.InClauseScope` — true iff a `BeginClauseScope` is active. Used by `ExplicitVisit(BooleanBinaryExpression)` to switch between WriteClauseKeyword (in-scope, right-aligned) and `EmitGeneratorRaw` fallback (D3).
- New visitor field `_caseDepth` — CASE-specific nesting counter (1 for top-level CASE, 2 for nested-in-THEN, etc.). Drives the WHEN pad and END pad independent of the global `_indentLevel`. The naive `_indentLevel`-based pad I started with looked right on the simple nested test but broke on the corpus smoke (CASE inside subquery WHERE got END at +4 instead of at the CASE column). Capture-then-update on `03-case-with-branches.sql` surfaced it.
- `EmitMultiLineGeneratorBody` retired (D7). After BBE dispatches through the visitor, the `EmitSearchConditionBody` default case only sees inherently single-line predicates; replaced by a 2-line inline generator-and-write.
- Plan deviation flagged in chat: D4's "top-level ORDER BY on SelectStatement" was based on a misread — ScriptDom puts `OrderByClause` on the abstract `QueryExpression` (inherited by both QuerySpec and BQE), so for UNIONs it lives on the BQE itself. Same shape (one-clause scope at statement indent), different dispatcher (BQE override, not SelectStatement).
- Tests: 13 new facts in `Tests/CaseAndUnionFormattingTests.cs`. CASE: searched, simple, no-else, nested, in-WHERE-comparison. UNION: top-level, three-arm, with top-level ORDER BY, inside subquery, INTERSECT. BooleanBinaryExpression: WHERE-AND, OR-AND-precedence, subquery-under-AND, deeply-nested defensive (5 chained AND + OR — proves recursion termination on real input). Full suite **80/80** (was 66).
- Regression harness: **48 / 0 / 5** — baseline preserved. CASE-bearing corpus file `03-case-with-branches.sql` was already canonical-matching via pre-4b-iv generator scaffold; output shape changes are AST-equivalent.
- Smoke captured for `03-case-with-branches.sql` (multiple CASEs: in SELECT, inside subquery WHERE comparison, in ORDER BY element). Surfaced and fixed the `_caseDepth` bug above. Final smoke output formats cleanly — every CASE END returns to its keyword's column, AND/OR-mixed conditions weren't tripped by this file.
- Known limitations went from 3 (subquery-under-AND/OR, BQE-as-subquery, AND/OR-multi-line-glitch) → 1 (AND/OR outside a clause scope, narrowed to ON / CASE WHEN / scalar contexts). The remaining one needs a redesign of ON's body model and is its own slice.
- **End-of-phase verification pending**: 80/80 tests green, harness 48/0/5, app launch pending.


### 4c — 2026-04-25

- **Step 0**: QueryDerivedTable-inside-JOIN-inside-CTE indent bug fixed, but the root cause was
  in the emitter's nested-pop path, not the visitor. Diagnosis: override fires correctly and
  `Indent()` is in the right position; the real issue was `SqlEmitter.EndClauseScope` leaving
  the inner buffer's full `captured * IndentSize` leading spaces on injected lines, which the
  parent then added its own `outerIndent + (maxKw + 1)` on top of — cumulative indent inflated
  per nesting level when the outermost scope had `captured > 0`. Fix: strip
  `parent.CapturedIndentLevel * IndentSize` leading spaces from each injected line. Inner
  SELECT in `CTE → SELECT FROM a LEFT JOIN (SELECT b.x FROM b WHERE b.y = 1) AS t` moves from
  col 19 → col 15 (outer FROM body col 11 + IndentSize 4). Test
  `Format_CteWithLeftJoinDerivedTable_InnerIndentsRelativeToParen` locks it. `SqlEmitterTests.
  ClauseScope_DoublyNestedRendersEachLevelIndependently` and `SubqueryFormattingTests.Format_
  DoublyNestedSubquery_BothInnerScopesAlignLocally` asserts updated to the new, consistent
  model (innermost SELECT shifts by 4 cols in each).
- Four new visitor overrides: `ExplicitVisit(InsertStatement)`, `ExplicitVisit(UpdateStatement)`,
  `ExplicitVisit(DeleteStatement)`, `ExplicitVisit(MergeStatement)`. Plus shared dispatchers
  `EmitOutputClause` (handles in-scope and out-of-scope paths), `EmitInsertSource`,
  `EmitValuesInsertSource`, `EmitMergeActionClause`, `EmitMergeAction`. `EmitFragmentDefault`
  gets four new routing branches.
- Grammar-correct emission order enforced: UPDATE / DELETE put OUTPUT between SET (or target)
  and FROM/WHERE — putting OUTPUT after WHERE breaks re-parse. INSERT puts OUTPUT between
  columns and source. MERGE puts OUTPUT after all WHENs.
- MERGE emits terminating `;` when `IncludeSemicolons = true` (default). MERGE requires it
  grammatically for parse. INSERT / UPDATE / DELETE don't emit `;` (consistent with SELECT).
- Tests: 15 new facts in `Tests/DmlFormattingTests.cs`. Plus 1 test added in step 0 and 2
  assertion updates for the emitter fix. Full suite **96 / 96** (was 80).
- Regression harness: **51 / 0 / 2** — the three MERGE-variant REGRESSION parse failures
  (`04-merge.sql`, `pathA.sql`, `original.sql`) all retire. Remaining 2 parse failures are the
  hogimn-baseline files carried since 4a.
- **End-of-phase verification complete**: 96/96 tests green, harness 51/0/2, app launched (standalone `lane_capacity` CTE-with-JOIN-derived-table reproduction format-smoke-tested against the formatter — inner SELECT at col 17, GROUP BY at col 15, all clean). The `test_sproc` visual in the running app was misleading: parse-fail on missing `;WITH` in a CREATE PROCEDURE body triggered the legacy fallback, not 4c's emission path. 4c-ii (pre-parse auto-repair) is the follow-up mini-slice to address that class of real-world parse failures before 4d.

### 4c-ii — 2026-04-25

- **Corpus**: 264 .sql files in `/Users/omer/Documents/Projects/SqlVersionControl/Sorgu` (user's working SQL directory). Probe categorization via throwaway `[Fact]`: 224 already parse, 40 fail. Failure breakdown — 35× error 46010 ("Incorrect syntax near X"), 3× 46005 ("Expected WINDOW/COPY"), 2× 46029 (EOF). Real causes: prose / chat-log / markdown content (≈16), bare procedure calls without EXEC (≈4), encoding / BOM (1), truncated files (2), pasted SELECT-list fragments (≈3), stray identifiers between statements (≈14). **Zero `;WITH` cases in this corpus** — the corpus is scratch/working files, not `sys.sql_modules` exports. Plan deviation flagged; corpus retained as regression guard for the 224 passing files.
- **Single rule landed**: `SqlPreRepair.Normalize` in `Services/Formatting/Visitor/SqlPreRepair.cs`. Detects `WITH <ident> [(<cols>)] AS (` at line start when prior non-blank-non-comment line lacks `;` / `GO` terminator, then prepends `;` preserving indentation and line endings. Wired into `ScriptDomFormatter.Format` once before the parse staircase; legacy fallback path still receives the original (un-repaired) sql.
- **Out of scope** per "if in doubt, don't fix": bare procedure calls without EXEC (ambiguous with identifier reference), BOM / U+FFFD strip (masks encoding bugs that should fail loudly), prose-mixed-with-SQL files (require human judgment).
- **Tests**: 11 new facts in `Tests/PreRepairTests.cs` — canonical positive (`BEGIN TRANSACTION\nWITH ...`), already-terminated guard, already-`;`-prefixed guard, top-of-batch guard, `WITH (NOLOCK)` table-hint guard, post-`GO` guard, CTE-with-column-list, CRLF preservation, empty-input, byte-identical pass-through on canonical SQL. Plus one corpus-regression `[Fact]` asserting that no Sorgu file regresses (224 → 224, delta 0; expected, since no `;WITH` cases present).
- Full suite **107 / 107** (was 96). Harness **51 / 0 / 2** unchanged. Probe file deleted.

### 4d-i + 4e-i — 2026-04-25

- **Slice motivation**: `test_sproc` visual smoke after 4c-ii showed the multi-branch CASE rendering compact-then-wrap-at-col-70 instead of visitor-style multi-line. Diagnosis (probe): the visitor's `ExplicitVisit(SearchedCaseExpression)` works correctly when reached, but in this corpus the entire `CreateProcedureStatement` was falling to `EmitGeneratorRaw` (line 1070) and rendering wholly via `Sql170ScriptGenerator`, never reaching the override. The right-aligned-clause-keyword styling we'd been seeing came from `AlignClauseBodies = true` on the generator, not from our visitor. Fix: route the procedure body through the visitor — needs both 4d (CREATE/ALTER PROCEDURE wrapper) and 4e (BEGIN/END + TRY/CATCH body blocks) since the body is `Procedure → BeginEndBlockStatement → TryCatchStatement → INSERT-with-CTE`. 4d-i alone would have stopped at the BEGIN/END level. Bundled.
- **Procedure overrides**: three thin `ExplicitVisit` overrides (`CreateProcedureStatement`, `AlterProcedureStatement`, `CreateOrAlterProcedureStatement`) all delegating to a shared `EmitProcedureBody(stmt, keywordPrefix)` helper. Header is piecewise: keyword + generator-rendered `ProcedureReference` + parameter list (via `EmitWrappedList` — short stays inline, long wraps one-per-line) + optional `FOR REPLICATION` + optional `WITH options` (generator-rendered, comma-joined) + `AS` + body recursion. CLR procedures (`MethodSpecifier != null`) emit `AS EXTERNAL NAME ...` and have no body recursion. Parameter rendering retains generator quirks (`VARCHAR (10)` extra space, `BIT=0` no spaces around equals) — out of scope, that's 4f's arbitrary-expression coverage.
- **Block overrides**: `ExplicitVisit(BeginEndBlockStatement)` emits `BEGIN`/`END` with a +IndentSize body. `ExplicitVisit(TryCatchStatement)` emits two halves via shared `EmitTryCatchHalf(opener, closer, list)`. Both recurse children via `EmitFragmentDefault`, which dispatches to overridden types and falls to generator for unmatched (`DECLARE`, `SET`, `IfStatement`, `WhileStatement`, transaction control, `RAISERROR`, `RETURN`, etc.). Graceful degradation verified: generator-fallback children land at the body indent column without indent inflation; nested unoverridden statements (e.g. an `IfStatement` containing its own BEGIN/END) render through one whole-statement generator call so internal indents stack correctly with our outer indent.
- **Atomic block guard**: `BeginEndAtomicBlockStatement` inherits from `BeginEndBlockStatement`. The `ExplicitVisit` dispatch would fire our override, which would silently drop atomic's `Options`. Type-check at top of `BeginEndBlockStatement` override falls atomic to `EmitGeneratorRaw` instead — correct content, atomic options preserved.
- **Bundled fix — DML overrides missing `WithCtesAndXmlNamespaces`** *(pre-existing 4c bug, surfaced by smoke test)*. `InsertStatement`, `UpdateStatement`, `DeleteStatement`, `MergeStatement` overrides didn't handle `stmt.WithCtesAndXmlNamespaces`, silently dropping CTEs that preceded the DML. Pattern is identical to `SelectStatement`'s existing handling at line 90–94. Added to all four overrides.
- **Bundled fix — body statements need `;` terminator** *(pre-existing generator behavior, surfaced by smoke test)*. ScriptDom's generator emits control-flow statements (ROLLBACK, COMMIT TRANSACTION, BEGIN TRANSACTION, DECLARE, SET) without trailing `;`, even with `IncludeSemicolons = true`. Previously, `CreateProcedureStatement` rendered the entire procedure via one generator call so internal text was self-consistent. With body recursion, each child is a separate `_generator.GenerateScript(stmt)` call — `ROLLBACK\nTHROW` re-parses as `ROLLBACK <name>` and breaks. Fix: new `SqlEmitter.EnsureTrailingSemicolon` — trims trailing whitespace, appends `;` if last non-whitespace char isn't already `;`. Called after every child in the three body-recursion sites (`EmitProcedureBody`, `BeginEndBlockStatement`, `EmitTryCatchHalf`). Idempotent for already-terminated emissions. Side benefit: also fixes the `BEGIN TRANSACTION\nWITH ...` parse break that would otherwise need SqlPreRepair on Format output.
- **Tests**: 6 new in `Tests/ProcedureFormattingTests.cs` (CREATE/ALTER/CREATE OR ALTER, short/long parameters, WITH ENCRYPTION) + 1 smoke (full test_sproc shape, asserts multi-line CASE + `BEGIN/COMMIT TRANSACTION;` + re-parses). 6 new in `Tests/BlockStatementTests.cs` (BEGIN/END one-statement, mixed-type body, nested BEGIN/END, TRY/CATCH basic, TRY/CATCH with nested BEGIN/END, atomic-block fallback). Full suite **121 / 121** (was 108). Harness **51 / 0 / 2** unchanged.
- **Visual-smoke finding (out of slice scope, queued)**: real-sproc visual smoke surfaced two cosmetic issues. (a) `IF NOT EXISTS (SELECT … FROM … WHERE …)` renders the inner subquery via the generator (since `IfStatement` isn't overridden), which uses left-aligned keywords with right-padded content — visually inconsistent with the visitor's right-aligned-keyword style elsewhere in the same procedure. Tracked as 4e-ii (override `IfStatement` + `WhileStatement`). (b) Multi-AND `JOIN … ON a = b AND x = 1` continuation lands at column 1 — the pre-existing BBE-in-ON quirk already documented in § Known limitations.
- **Out of scope / deferred**: CREATE/ALTER VIEW (4d-ii), CREATE/ALTER FUNCTION (4d-iii — incl. inline TVF and multi-statement TVF), CREATE/ALTER TRIGGER (4d-iv), CREATE/ALTER TABLE (4d-v), 4e-ii (IF/WHILE), 4e-iii (DECLARE/SET/etc. taste refinements), parameter formatting (4f). Atomic-block formatted output stays generator-rendered.

### 4e-ii — 2026-04-25

- **Slice motivation**: 4d-i + 4e-i visual smoke (entry above) flagged that `IF NOT EXISTS (SELECT … FROM … WHERE …)` rendered the inner subquery via the generator's left-aligned-keyword style, visually inconsistent with the surrounding visitor right-aligned style. `IfStatement` / `WhileStatement` weren't overridden — `EmitFragmentDefault` fell to `EmitGeneratorRaw`, which renders the entire IF (including the inner subquery) via Sql170ScriptGenerator.
- **Conditional overrides**: `ExplicitVisit(IfStatement)` and `ExplicitVisit(WhileStatement)` via two shared helpers — `EmitConditionalBody` (BEGIN/END body lands at IF column; single-statement body wrapped in `Indent()`) and `EmitConditionalPredicate`. The predicate dispatcher is *separate from* `EmitSearchConditionBody` because IF/WHILE have no clause scope (opening one for a single `IF` keyword would set `maxKw=2` and pin continuations at col 3 — visually awkward). Same dispatch table minus the BBE branch (BBE inside a clause scope right-aligns AND/OR with `WriteClauseKeyword`; without a scope it'd fall to `EmitGeneratorRaw` and produce the same left-aligned shape we're trying to escape — corpus-driven if it surfaces). New branch: `BooleanNotExpression` decomposes to `NOT ` + recurse on the inner expression, which lets `IF NOT EXISTS (subq)` route through `ExplicitVisit(ExistsPredicate)` and break-to-block as the WHERE-EXISTS path does today.
- **Bundled fix — AtLineStart guard before `)` in 4b-ii overrides**: `ScalarSubquery` / `InPredicate` / `ExistsPredicate` were unconditionally writing a NewLine before the closing `)`. Inside a parent clause scope (today's only call site for these), the inner QuerySpec's flush injects lines into the parent buffer and the explicit NewLine is needed. At top level (4e-ii's new call site, IF/WHILE predicates), the inner QuerySpec pops directly to `_sb` with a trailing `\n`, so the explicit NewLine creates a blank line before `)`. Same pattern as the 4b-iii-b `CommonTableExpression` fix: guard with `if (!_emitter.AtLineStart) _emitter.NewLine()`. AtLineStart returns false inside any clause scope — in-scope behavior unchanged.
- **Tests**: 9 new in `Tests/ConditionalStatementTests.cs` (IF single-stmt / IF BEGIN/END / IF/ELSE × 2 body shapes / IF EXISTS subq / IF NOT EXISTS subq / ELSE IF stairstep lock / WHILE single-stmt / WHILE BEGIN/END). Each asserts exact-equals output and re-parses. Plus 1 smoke in `Tests/ProcedureFormattingTests.cs::Format_TestSproc_IfNotExistsInsideTry_RightAligns` (a sproc body with `IF NOT EXISTS (subq) BEGIN INSERT … END` inside `BEGIN TRY`, asserts inner subquery's right-aligned `FROM` / `WHERE` / `AND`). Full suite **131 / 131** (was 121). Harness **51 / 0 / 2** unchanged.
- **New Known limitation — ELSE IF stairstep**: `IF a ELSE IF b ELSE c` parses with the second `IF` as `ElseStatement`; natural recursion places it one indent level deeper. Real-world T-SQL uses ELSE IF chains constantly — flagged prominently in § Known limitations with a 10-line fix sketch. Out of 4e-ii scope; corpus-driven.
- **Out of scope / deferred**: ELSE IF flattening (above); 4e-iii (DECLARE / SET / transaction control taste refinements); BBE-in-IF-predicate (no clause scope to right-align against — same shape as the existing AND/OR-in-ON quirk).

### 4e-ii-b — 2026-04-25

- **Slice motivation**: 4e-ii visual smoke (real staffing-report sproc) showed the formatter glues procedure body siblings with no separator — a TRY body with `INSERT … VALUES … COMMIT TRANSACTION` and a CATCH body with `IF / DECLARE / DECLARE / INSERT / RAISERROR / RETURN` all run together as a wall of statements. Hard to scan, especially the boundaries between block-level statements (INSERTs, IFs) and the surrounding control-flow / variable-management lines. Out-of-slice gap from 4d-i: `ExplicitVisit(TSqlBatch)` separates batches with a blank line, but the three intra-body recursion sites (`EmitProcedureBody`, `BeginEndBlockStatement`, `EmitTryCatchHalf`) didn't.
- **Spacing rule**: blank line between two adjacent body siblings iff at least one is block-level. Block-level = multi-line in our output: `SelectStatement` / `InsertStatement` / `UpdateStatement` / `DeleteStatement` / `MergeStatement` / `IfStatement` / `WhileStatement` / `BeginEndBlockStatement` (incl. atomic) / `TryCatchStatement`. Everything else (DECLARE / SET / RETURN / transaction control / RAISERROR / EXEC / BREAK / CONTINUE / etc.) is single-liner. Phrased symmetrically: "blank line before *or* after a block-level statement, not between consecutive single-liners." Adjacent DECLAREs hug; an `IF cond ROLLBACK` next to a `RETURN -1` hugs only on one side (the IF side gets the blank); two INSERTs in a row get a blank between them.
- **Implementation**: shared `EmitBodyStatements(StatementList?)` helper + `IsBlockLevelStatement(TSqlStatement)` predicate. All three former for-loops collapse into `EmitBodyStatements(list)`. The blank-line decision is made before each child after the first; `EnsureTrailingSemicolon` already leaves the cursor at line-start, so an extra `NewLine()` produces one blank line.
- **Tests**: 6 new in `Tests/BlockStatementTests.cs` covering pairwise spacing decisions — DECLARE+DECLARE tight, DECLARE+INSERT spaced, INSERT+SET spaced (the rule's interesting case), two-blocks spaced, IF+RETURN spaced, plus a mixed cluster matching the staffing-report CATCH shape. Existing `Format_BeginEnd_MixedOverriddenAndGeneratorFallback` updated (was DECLARE/SET/SELECT tight, now DECLARE/SET tight + blank + SELECT). Full suite **131 → 137**, all pass. Harness **51 / 0 / 2** unchanged.
- **Out of scope / deferred**: configurable spacing (off-toggle for users who want tight); category-based rule with finer granularity (e.g. blank line on statement-kind change even between two single-liners) — corpus-driven if it surfaces.

### 4d-ii — 2026-04-25

- **Slice motivation**: real `ALTER VIEW` from `Sorgu/view_duplicates.sql` was hitting `EmitGeneratorRaw` (no view override). Whole view rendered through `Sql170ScriptGenerator`, so the body SELECT/JOIN/WHERE didn't right-align and didn't pick up the visitor's CTE/CASE/subquery formatting — visually inconsistent with the surrounding visitor output anywhere a view sat next to a procedure or DML.
- **Three view overrides** + shared `EmitViewBody(ViewStatementBody, keywordPrefix)` helper. Same shape as `EmitProcedureBody`: header keyword + `SchemaObjectName` + optional column list + optional `WITH` options + `AS` + body via `EmitFragmentDefault` + optional `WITH CHECK OPTION` trailer. Three new branches in `EmitFragmentDefault` (Create / Alter / CreateOrAlter View).
- **Probe deviations** flagged before coding: (a) the property is `WithCheckOption`, not `IsCheckOption` as predicted; (b) `ViewStatementBody.IsMaterialized` exists (Synapse materialized views) — defensively falls back to `EmitGeneratorRaw` at the top of the helper, same pattern as `BeginEndAtomicBlockStatement`. `ViewOption` is a wrapper over a `ViewOptionKind` enum; generator-rendered for consistency with procedure WITH options.
- **Column list shape**: inline-only via per-identifier generator scaffold + comma-join — direct mirror of `CommonTableExpression.Columns` rendering. No `EmitWrappedList`. Promotion deferred jointly with CTE columns until corpus surfaces a long view column list.
- **Body indent**: flat — no `Indent()` around the body SELECT, matches SSMS / GittyExport convention. The body's `QuerySpecification` opens its own clause scope at `_capturedIndentLevel = 0`, so SELECT/FROM/WHERE right-align with their own local `maxKw`.
- **WITH CHECK OPTION**: emits on its own line at col 0 after the body. No trailing `;` is emitted — `SelectStatement` body doesn't emit one and the parser doesn't require one for views.
- **Bundled fix — `QueryParenthesisExpression` unwrap in `ExplicitVisit(SelectStatement)`** *(pre-existing data-loss bug, surfaced by `Sorgu/view_duplicates.sql` smoke)*. Real-world `ALTER VIEW … AS (SELECT … WHERE … GROUP BY … HAVING …)` shapes parse with `SelectStatement.QueryExpression` as a `QueryParenthesisExpression` wrapping the inner QuerySpec. Pre-fix, the dispatcher's `is QuerySpecification` check returned false, fell through to `EmitGeneratorRaw`, and the generator's bare-fragment quirk silently dropped trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY) — actual content lost in the formatted output. Fix: peel `QueryParenthesisExpression` layers off `statement.QueryExpression` before the trip-flag check and before the QuerySpec dispatch. Guard: only unwrap when the parens carry no clauses of their own (`OrderBy` / `Offset` / `For` at the parens level fall through to generator unchanged). Dropping the parens is consistent with the formatter's ScriptDom-canonical philosophy — SSMS strips them too.
- **Tests**: 10 new facts in `Tests/ViewFormattingTests.cs` (capture-and-lock pattern; no `Assert.True(true)` left in committed file). Minimal CREATE / column list / WITH SCHEMABINDING / multi-option / ALTER / CREATE OR ALTER / body has WHERE+AND / WITH CHECK OPTION / body has CTE / realistic body with JOIN+WHERE+GROUP BY+HAVING. Plus 1 new fact in `Tests/QuerySpecificationFormattingTests.cs` (`Format_SelectWithParenthesizedQuery_PreservesAllClauses`) locking the unwrap fix. All exact-equals + `Assert.NotNull(ReParse(output))`. Full suite **137 → 148**, all pass. Harness **51 / 0 / 2** unchanged.
- **Out of scope / deferred**: indexed-view trailer index DDL (separate `CreateIndex…` statements, not children of the view AST); long-column-list wrap (joint with CTE columns); comments inside view body (4g); materialized views (defensive fallback retained); `QueryParenthesisExpression` carrying its own ORDER BY/OFFSET/FOR (rare; falls through to generator).

### Problem

ScriptDom's parser is strict — it rejects SQL that the SQL Server engine happily executes. Real sprocs pulled from `sys.sql_modules` or scripted via SSMS often contain forms like `WITH cte AS (...)` without a preceding `;`, which the engine tolerates but ScriptDom doesn't. This causes the formatter to fall back to legacy on real production sprocs, making the entire Path B rewrite invisible to users working with real code.

### Approach

A static pre-repair pass that fixes known-benign parser quirks before handing text to `TSql170Parser`. Strict whitelist of mechanical fixes — not a general SQL normalizer.

`SqlPreRepair.Normalize(string sql) : string` — called by `ScriptDomFormatter.Format` before the parse. If the repaired text still fails to parse, fall back to legacy as today.

### Scope

1. Run the full GittyExport corpus through the parser. Collect every parse failure. Categorize the causes.
2. Implement a fix for each category as a narrow regex or string replacement. Expected 3–5 patterns. The first known one is `;WITH` — a `WITH` keyword not preceded by `;` or a statement-terminating token.
3. Each fix must be scoped narrowly enough that it cannot mangle valid SQL. If in doubt, don't fix — let it fall back to legacy.
4. Add unit tests: one per quirk pattern (repaired text parses successfully), plus a guard test proving valid SQL passes through unchanged.

### Architecture

- New file: `Services/Formatting/Visitor/SqlPreRepair.cs` (internal, static).
- Single entry point: `static string Normalize(string sql)`.
- Called once in `ScriptDomFormatter.Format`, before the parse staircase.
- Does not touch the visitor, emitter, or comment attacher.
- Corpus-driven — only fix what actually fails. Do not anticipate patterns.

### Budget

Half a day. The corpus analysis is the bulk of the work; each fix is trivial once the pattern is identified.

### Sequencing

Do this before 4d. Every subsequent slice that smoke-tests against real sprocs will hit the same parse-failure wall if this isn't in place. The formatter must actually work on real production SQL before adding more formatting features on top.
