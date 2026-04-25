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
- `ExplicitVisit(CreateFunctionStatement)` / `ExplicitVisit(AlterFunctionStatement)` / `ExplicitVisit(CreateOrAlterFunctionStatement)` *(4d-iii)* — three thin overrides delegating to `EmitFunctionBody(FunctionStatementBody, keywordPrefix)`. `FunctionStatementBody` is a sibling of `ProcedureStatementBody` under `ProcedureStatementBodyBase`, so `Parameters` / `MethodSpecifier` / `StatementList` are inherited; `EmitProcedureParameterBody` is reused as-is. Header: keyword + generator-rendered `Name` (`SchemaObjectName`) + parameter list (always parenthesised — empty `()` inline, non-empty multi-line with paren on its own line and params indented; functions reject the proc-style no-paren form at parse time) + `RETURNS <type>` (`ScalarFunctionReturnType` → DataType; `SelectFunctionReturnType` → literal `TABLE`; `TableValuedFunctionReturnType` → generator-render `DeclareTableVariableBody` for the `@t TABLE (cols)` shape) + optional `OrderHint` (rare) + optional `WITH <opts>` (generator-rendered, comma-joined) + `AS`. CLR functions (`MethodSpecifier != null`) emit `AS EXTERNAL NAME <spec>` and return — same shape as procs. Body branches by `ReturnType` shape: `SelectFunctionReturnType` (inline TVF) emits `RETURN (` + NewLine + `Indent()` + `EmitFragmentDefault(SelectStatement)` + `AtLineStart` guard + `)` (mirrors ScalarSubquery break-to-block); scalar / multi-stmt TVF route through `EmitBodyStatements(StatementList)` and the existing `BeginEndBlockStatement` override emits the wrapping BEGIN/END (ScriptDom captures function BEGIN/END as `StatementList[0]`, not implicit). Defensive `EmitGeneratorRaw` for unknown `ReturnType` subclasses.
- `ExplicitVisit(CreateTriggerStatement)` / `ExplicitVisit(AlterTriggerStatement)` / `ExplicitVisit(CreateOrAlterTriggerStatement)` *(4d-iv)* — three thin overrides delegating to `EmitTriggerBody(TriggerStatementBody, keywordPrefix)`. `TriggerStatementBody` is a `TSqlStatement` subclass; the three concrete types are siblings (no shared base with procs / views / funcs). Header (T-SQL grammar order, source-order observed): keyword + generator-rendered `Name` + `ON ` + generator-rendered `TriggerObject` (handles the `dbo.t` / `DATABASE` / `ALL SERVER` literals via `TriggerScope` internally) + optional `WITH <opts>` (generator-rendered, comma-joined — `TriggerOption` for `ENCRYPTION`, `ExecuteAsTriggerOption` for `EXECUTE AS …`) + timing keyword (from `TriggerType` enum: `AFTER` / `INSTEAD OF` / `FOR`) + comma-joined event list (each `TriggerAction` generator-rendered — handles INSERT/UPDATE/DELETE for DML, `Event` with `EventTypeContainer` / `EventGroupContainer` for DDL, and `LogOn` uniformly) + optional `NOT FOR REPLICATION` (from `IsNotForReplication` flag) + `AS` + body via `EmitBodyStatements(StatementList)`. Body shape mirrors procs (flat list, BEGIN/END captured as `StatementList[0]` when source has it), not functions (which always wrap). No parameter list — triggers don't have parameters. `WITH APPEND` is rejected by the ScriptDom 170 parser; not formattable, no test coverage.
- `ExplicitVisit(CreateTableStatement)` *(4d-v)* — single override delegating to `EmitCreateTableBody`. Header: `CREATE TABLE` + generator-rendered `SchemaObjectName` + `EmitTableDefinitionBody` for the parenthesised `( cols, table-constraints, indexes )` block, then optional `ON <fg>` / `TEXTIMAGE_ON <fg>` / `FILESTREAM_ON <fg>` each on its own line at column 0, then optional table-level `WITH (<opts>)` (`MemoryOptimizedTableOption`, `SystemVersioningTableOption`, etc. — each generator-rendered, comma-joined). `( cols, constraints, indexes )` body uses no blank-line separator between groups (D2 — matches SSMS canonical and the Sorgu corpus). External / graph / ledger tables fall through to defensive generator fallback (out of scope this slice).
- `ExplicitVisit(AlterTableAddTableElementStatement)` / `ExplicitVisit(AlterTableDropTableElementStatement)` / `ExplicitVisit(AlterTableAlterColumnStatement)` / `ExplicitVisit(AlterTableSwitchStatement)` / `ExplicitVisit(AlterTableTriggerModificationStatement)` / `ExplicitVisit(AlterTableConstraintModificationStatement)` *(4d-v)* — six per-subtype overrides, each its own helper. ScriptDom carves ALTER TABLE into six concrete statement types (no polymorphic body), so per-type emission is cleaner than a dispatcher. Common shape: `EmitAlterTableHeader` writes `ALTER TABLE <name>` + NewLine; the action keyword (`ADD` / `DROP COLUMN|CONSTRAINT` / `ALTER COLUMN` / `SWITCH` / `ENABLE|DISABLE TRIGGER` / `CHECK|NOCHECK CONSTRAINT`) lands on the next line at column 0. `EmitExistingRowsCheck` emits `WITH CHECK ` / `WITH NOCHECK ` prefix on the action line for ADD and CHECK CONSTRAINT branches when `ExistingRowsCheckEnforcement` is set. ADD with multiple elements routes through `EmitTableDefinitionBody` (same shape as CREATE TABLE); ADD with a single element renders inline.
- `EmitTableDefinitionBody(TableDefinition)` *(4d-v)* — shared body for the parenthesised block. NewLine + `(` on its own line + `Indent()` block over `ColumnDefinitions` then `TableConstraints` then `Indexes` then optional `SystemTimePeriod` (PERIOD FOR SYSTEM_TIME), comma-trailing per D3, no blank-line separator (D2). Closing `)` on its own line at outer indent. Three call sites: `EmitCreateTableBody`, `EmitAlterTableAdd*` (multi-element branch), `EmitFunctionBody`'s multi-stmt TVF `RETURNS @t TABLE (...)` block (4d-v backfill — replaced the per-column generator-fallback that produced squashed alignment artifacts).
- `EmitColumnDefinition(ColumnDefinition)` *(4d-v)* — generator-render the column as a single line. Identifier + type + collation + identity + nullable + default + computed-AS + inline constraints + inline INDEX all render correctly via `Sql170ScriptGenerator` when called per-column (the column-alignment padding artifact only manifests when the generator renders a parent `TableDefinition` wholesale).
- `EmitConstraintDefinition(ConstraintDefinition)` *(4d-v)* — dispatches: `UniqueConstraintDefinition` (PK / UQ — distinguished by `IsPrimaryKey` and `IndexType.IndexTypeKind`) routes to `EmitUniqueConstraintDefinition` for the WITH-options-and-ON-filegroup wrap logic. `ForeignKeyConstraintDefinition` / `CheckConstraintDefinition` / `DefaultConstraintDefinition` have no per-constraint WITH/ON tail — generator-rendered wholesale.
- `EmitUniqueConstraintDefinition(UniqueConstraintDefinition)` *(4d-v)* — D1 option C wrap rule. Header (`[CONSTRAINT name] PRIMARY KEY|UNIQUE [CLUSTERED|NONCLUSTERED] (cols ASC|DESC,...)`) is built explicitly (not generator-rendered) so we can measure its length. WITH options + ON filegroup render inline (continuing the header line) iff `currentIndent + headerLength + inlineExtra ≤ MaxLineLength`; else WITH-options wrap one-per-line at +2*IndentSize and ON-filegroup trails on its own line at +IndentSize. Real corpus PKs with the full SSMS 6-option WITH block always wrap (~215 chars); short UQs with one or two options stay inline.
- `ExplicitVisit(DeclareVariableStatement)` *(4e-iii)* — `DECLARE @var <type> [= <init>][, ...]`. Drops the generator-injected `AS` keyword (source-faithful, corpus-matching). Multi-var wrap shape: when the inline form would exceed `MaxLineLength * 2/3` (same threshold as `EmitWrappedList`), DECLARE sits alone on its line and each declaration lands at +`IndentSize`, comma-trailing — matches the corpus pattern in `Sorgu/usp_daily_package_info.sql:12-15` and `Sorgu/2161.sql:25-39`. Each `DeclareVariableElement` rendered through `RenderDeclarationText` (variable name + generator-rendered DataType + optional ` = ` + generator-rendered Value) — single source for both wrap measurement and emission.
- `ExplicitVisit(DeclareTableVariableStatement)` *(4e-iii)* — `DECLARE @t TABLE` header line, then `EmitTableDefinitionBody` (4d-v helper) for the parenthesised column / constraint block. Reuses the same per-column wrap rules as CREATE TABLE / multi-stmt TVF — fixes the generator's column-alignment padding artifact (`id   INT           ,`) without new code.
- `ExplicitVisit(RollbackTransactionStatement)` *(4e-iii)* — `ROLLBACK TRANSACTION [name]`. The generator drops the keyword on bare ROLLBACK (`ROLLBACK TRAN;` → `ROLLBACK`); we re-emit it explicitly so the form is symmetric with BEGIN / COMMIT / SAVE TRANSACTION (which the generator handles cleanly). The AST does not preserve the source distinction between `TRAN` and `TRANSACTION`, so always emit the long form.
- `ExplicitVisit(PivotedTableReference)` *(4f)* — `<source> PIVOT (agg(val) FOR col IN (v1, v2, ...)) AS alias`. Source recurses through `EmitTableReferenceBody` so nested `QueryDerivedTable` / `PivotedTableReference` etc. dispatch correctly. PIVOT clause renders inline when the assembled length fits under `MaxLineLength` (full width — the IN-list lives in the FROM body, not behind a right-aligned keyword block, so the 2/3 threshold from `EmitWrappedList` doesn't apply); otherwise the IN-list breaks one value per line at `+IndentSize` from the FROM body column with the closing `))` and ` AS alias` returning to the body column. `aggIdentifier(valueArgs)` is hand-assembled (`SUM(amt)` rather than the generator's `SUM (amt)`) — single-source-of-truth via `JoinPart` / `RenderEach` helpers covering both wrap measurement and emission. `ForPath` (graph SQL) trips a defensive `EmitGeneratorRaw` fallback. Wired into both `EmitTableReferenceBody` and `EmitFragmentDefault`.
- `ExplicitVisit(UnpivotedTableReference)` *(4f)* — mirror of PIVOT. Singular `ValueColumn` (Identifier) and `ColumnReferenceExpression` `InColumns` (vs PIVOT's Identifier list). Same wrap rule; same source recursion; same `ForPath` defensive fallback.
- `ExplicitVisit(SetVariableStatement)` *(4f)* — only `Expression is ScalarSubquery` triggers the override; everything else (literals, expressions, `+=` / `-=` / etc., CURSOR, `NEXT VALUE FOR`, parens-around-scalar) generator-passes through. Subquery shape mirrors 4b-ii's `ScalarSubquery`: `SET @var op (\n    <body>\n)`. Single break-to-block pattern across the formatter — no second style invented for SET. `AssignmentOperator` switch covers all 9 `AssignmentKind` enum values.

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
  - ~~**4d-iii**~~: Landed 2026-04-25. `CreateFunctionStatement`, `AlterFunctionStatement`, `CreateOrAlterFunctionStatement` via shared `EmitFunctionBody` helper. Three `ReturnType` shapes branch on subclass (scalar / inline TVF / multi-stmt TVF). Inline TVF body SELECT routes through `EmitFragmentDefault` (mirrors ScalarSubquery break-to-block); scalar / multi-stmt TVF body recurses via `EmitBodyStatements` and the existing `BeginEndBlockStatement` override emits the wrapping BEGIN/END.
  - ~~**4d-iv**~~: Landed 2026-04-25. `CreateTriggerStatement`, `AlterTriggerStatement`, `CreateOrAlterTriggerStatement` via shared `EmitTriggerBody` helper. DML and DDL/logon triggers handled uniformly: `TriggerObject` and `TriggerActions[]` are generator-rendered (the generator handles `Normal`/`Database`/`AllServer` scope literals and the `EventTypeContainer`/`EventGroupContainer` event-list variants without needing per-shape branches). Body recursion via `EmitBodyStatements` reuses the existing BEGIN/END override.
  - ~~**4d-v**~~: Landed 2026-04-25. `CreateTableStatement` + six `AlterTable*Statement` subtypes. New shared helpers `EmitTableDefinitionBody` / `EmitColumnDefinition` / `EmitConstraintDefinition` / `EmitUniqueConstraintDefinition`. D1 option C: constraint WITH-options inline if total fits MaxLineLength, else wrap one-per-line at +2*IndentSize with ON-filegroup on its own line at +IndentSize. D2: no blank-line separator between column block and constraint block (matches SSMS canonical). D3 backfill: multi-stmt TVF column DDL re-uses `EmitTableDefinitionBody`, retiring the squashed-column generator artifact at `EmitFunctionBody`'s `TableValuedFunctionReturnType` branch (`Format_CreateFunction_MultiStmtTvf` test expected updated). 4d completes the major DDL-object rectangle (proc / view / func / trigger / table). Out of scope: `CreateExternalTableStatement`, graph (`AsNode`/`AsEdge`), ledger, `CREATE TYPE ... AS TABLE`.
- **4e**: Body-block and control-flow statements.
  - ~~**4e-i**~~: Landed 2026-04-25. `BeginEndBlockStatement` (with `BeginEndAtomicBlockStatement` fallback guard), `TryCatchStatement`. Bundled with 4d-i.
  - ~~**4e-ii**~~: Landed 2026-04-25. `IfStatement`, `WhileStatement` via shared `EmitConditionalBody` + `EmitConditionalPredicate` helpers. Bundled fix: AtLineStart guard before `)` in `ScalarSubquery` / `InPredicate` / `ExistsPredicate` (latent 4b-ii bug — surfaced when those overrides got called outside a parent clause scope, leaving a blank line before the closing `)`). New Known-limitation entry: ELSE IF stairstep.
  - ~~**4e-ii-b**~~: Landed 2026-04-25. Vertical spacing rule for body recursion. Shared `EmitBodyStatements` helper used by `EmitProcedureBody` / `BeginEndBlockStatement` / `EmitTryCatchHalf`. Rule: blank line between siblings iff at least one is "block-level" (multi-line in our output: SELECT/INSERT/UPDATE/DELETE/MERGE/IF/WHILE/BEGIN-END/TRY-CATCH). Single-liners (DECLARE/SET/RETURN/transaction-control/RAISERROR) stay tight as a group; block statements get breathing room on both sides.
  - ~~**4e-iii**~~: Landed 2026-04-25. Per-statement overrides for the small fry that the generator was *not* emitting cleanly: `DeclareVariableStatement` (drops `AS`, wraps multi-var per A1 corpus shape), `DeclareTableVariableStatement` (reuses `EmitTableDefinitionBody` for clean column DDL), `RollbackTransactionStatement` (generator drops the `TRANSACTION` keyword — re-emit to keep symmetry with BEGIN/COMMIT/SAVE). 8 of the 13 statement types originally listed turned out to render correctly through the generator and got no override (`DeclareCursorStatement`, `SetVariableStatement`, `PredicateSetStatement`, `SetTransactionIsolationLevelStatement`, `ReturnStatement`, `BeginTransactionStatement`, `CommitTransactionStatement`, `SaveTransactionStatement`, `ThrowStatement`, `RaiseErrorStatement`). Out of scope / deferred to 4f: `SetVariableStatement` with subquery RHS still uses the generator's left-aligned-keyword style internally — a real visual inconsistency, but resolution overlaps with arbitrary `ScalarExpression` (4f's territory).
- ~~**4f**~~: Landed 2026-04-25. `PivotedTableReference`, `UnpivotedTableReference` (PIVOT / UNPIVOT with full-`MaxLineLength` IN-list wrap), `SetVariableStatement` (subquery-RHS-only break-to-block mirroring 4b-ii's ScalarSubquery). Probe-driven scope shrink from the original "PIVOT / APPLY / ParenthesisExpression / remaining expression types" — APPLY-with-function (`SchemaObjectFunctionTableReference` on UnqualifiedJoin's RHS) was already correct via the 4b-iii-a `EmitTableReferenceBody` default branch, and `ParenthesisExpression` round-trips through the generator unchanged. Both retained as regression tests rather than overrides. Bundled fix: `QuerySpecification` niche-feature fallback now wraps into a synthetic `SelectStatement` before generator-rendering — bare-QuerySpec drops trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY) when TOP / OFFSET / FOR is present; was latent in 4b-ii's `ScalarSubquery` path until SetVariable subquery-RHS surfaced it. Out of scope and explicitly deferred: `BooleanBinaryExpression` outside a clause scope (the AND/OR-at-col-1 quirk in ON / CASE-WHEN / scalar contexts — structural, not expression-type, slice).
- ~~**4f-ii**~~: Landed 2026-04-25. `QuerySpecification` niche-feature trip-flag retired entirely. `TopRowFilter` renders inline within the SELECT clause body (`TOP <expr> [PERCENT] [WITH TIES]`); `OffsetClause` becomes its own clause keyword after ORDER BY (one body line: `OFFSET <expr> ROWS [FETCH NEXT <expr> ROWS ONLY]`); `ForClause` becomes its own clause keyword after OFFSET (body via prefix-strip of generator output). `QuerySpecRequiresFallback` and both its callers (SelectStatement-level + QuerySpec-level fallbacks) deleted. Motivation: 4f's synthetic-wrap routed niche-feature subqueries through `Sql170ScriptGenerator` with `AlignClauseBodies=true`, which produces left-aligned-keyword style ("Style 2") incompatible with the visitor's right-aligned-keyword staircase ("Style 1") — visible in any `LEFT JOIN (SELECT TOP N ...) alias`, `SET @x = (SELECT TOP 1 ...)`, or `SELECT (SELECT TOP 1 ...) FROM t` shape. Probe-driven scope correction: the originally-tight scope (only OFFSET) would have left TOP-in-subquery still leaking Style 2; full scope retires the leak completely. Plan deviation flagged: corpus harness re-bake step turned out unnecessary — the harness compares canonical AST forms (round-trip via `Sql170ScriptGenerator`), not byte-level output, so aesthetic changes pass through; harness stayed 51 / 0 / 2 with no intervention. Out of scope and explicitly deferred: INSERT / UPDATE / DELETE `TopRowFilter` trip-fallbacks at TSqlFormatterVisitor.cs:812 / 857 / 899 — same Style-2 leak, but on `InsertSpecification` / `UpdateSpecification` / `DeleteSpecification` (different specs, separate code paths, rarer corpus shapes like `UPDATE TOP (N) ...`). Carry-over to 4f-iii or bundled with the BBE-quirk slice. Function-arg `ScalarSubquery` (e.g. `STUFF((SELECT ... FOR XML PATH('')), ...)` corpus pattern) keeps generator-style left-aligned because the function call is generator-rendered as one expression and the inner subquery never reaches the visitor's dispatch — captured as a known limitation in `Format_StuffForXmlPath_Corpus_KnownGeneratorPassthrough`.
- ~~**4f-iii**~~: Landed 2026-04-25. INSERT / UPDATE / DELETE `TopRowFilter` trip-fallbacks retired. All three were `if (spec.TopRowFilter != null) { EmitGeneratorRaw(stmt); return; }` bails at TSqlFormatterVisitor.cs:824/869/911 — the same Style-2 leak 4f-ii eliminated for `QuerySpecification`, on the three remaining `*Specification` types. Reused `EmitTopRowFilter` (4f-ii) without modification. INSERT places TOP between INSERT keyword and INTO/OVER (no clause scope — INSERT is no-scope). UPDATE places TOP in body of UPDATE keyword line (inside the existing UPDATE/SET/FROM/WHERE clause scope), so scope `maxKw` stays at UPDATE/WHERE width — SET/FROM/WHERE right-align unchanged. DELETE places TOP in body of DELETE keyword line in both shapes: simple form (`spec.FromClause == null`) emits `DELETE TOP <expr> FROM <target>` on one body line; extended form (with explicit FromClause) emits `DELETE TOP <expr> <target>` on the DELETE keyword line, then FROM on its own keyword line. Probe (`Tests/TEMP_FourFiiiProbe.cs`, deleted at slice end) confirmed: all three are `TopRowFilter` type (helper reusable as-is); current trip-fallbacks were firing pre-fix; corpus contains zero `INSERT TOP` / `UPDATE TOP` / `DELETE TOP` instances (synthetic-input only). 10 new exact-equals tests in `Tests/DmlFormattingTests.cs`. Suite **223 → 233**. Harness **51 / 0 / 2** unchanged (canonical-AST equivalence is invariant under aesthetic-only changes — same as 4f-ii). No new dispatch entries (these are existing statement overrides; the silent-fallthrough trap doesn't apply). `EmitGeneratorRaw` survives only for unrelated paths (PIVOT `ForPath`, BBE outside clause scope, `EmitFragmentDefault`'s final fallthrough, etc.) — the BBE-outside-clause-scope path is the next slice target (Branch C / "BBE-quirk").
- ~~**4f-iv**~~: Landed 2026-04-25. JOIN ON multi-AND/OR col-0 leak (Branch C / "BBE-quirk") retired. `ExplicitVisit(QualifiedJoin)` now detects `qj.SearchCondition is BooleanBinaryExpression` and breaks ON to its own line in a synthetic clause scope, so AND/OR right-aligns with ON via existing `WriteClauseKeyword`. Single-comparison ON keeps the inline-or-long-break heuristic from 4b-iii-a — gate is BBE-specific. Probe (`Tests/TEMP_BbeQuirkProbe.cs`, deleted at slice end) walked 11 shapes including the corpus ground-truth from `Sorgu/Buyuk Kucuk Kasa Yeni.sql:34`. Probe-driven scope reduction (memory `feedback_pushback_when_objections_are_weak`): the original Branch C plan covered CASE WHEN / IF / WHILE / scalar BBE contexts too; probe showed those render single-line via `EmitInlineBooleanScaffold` squash (line 624) — no col-0 leak, just overflow-without-break for long predicates. Distinct cosmetic concern, deferred to its own slice if corpus surfaces it. `EmitGeneratorRaw(bbe)` bail at TSqlFormatterVisitor.cs:515 retained as safety net for `EmitFragmentDefault` catchall paths. 6 new exact-equals tests in `Tests/JoinAndCteFormattingTests.cs` (single-AND / triple-AND / LEFT JOIN multi-AND / mixed-AND-OR / corpus UPDATE-FROM-JOIN shape / single-cond-no-fire regression-guard). Suite **233 → 239**. Harness **51 / 0 / 2** unchanged. Zero existing tests touched (BBE gate preserves all locked single-cond ON shapes from 4b-iii-a).
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
- `OFFSET` *(4f-ii)* — clause-keyword line after `ORDER BY`. Body holds `OFFSET <expr> ROWS [FETCH NEXT <expr> ROWS ONLY]` inline (FETCH never splits to its own line — corpus has zero usages and SSMS canonical keeps tight). `EmitOffsetClauseBody` generator-renders the expression sub-fragments.
- `FOR` *(4f-ii)* — clause-keyword line after `OFFSET`. Body emitted by generator-rendering the entire `ForClause` and stripping the leading `"FOR "`. `XmlForClauseOption` and `JsonForClauseOption` enum-and-literal rendering is non-trivial; the generator already handles `AUTO` / `PATH` / `ELEMENTS` / `ROOT('r')` / `BINARY BASE64` / `INCLUDE_NULL_VALUES` etc. Inherited generator-ism: stray space before parens (`PATH ('')` vs `PATH('')`) — minor, recorded in § Known limitations.
- `TOP` *(4f-ii)* — inline within the SELECT clause body, after `[ALL|DISTINCT]` and before SelectElements. Form: `TOP <expr> [PERCENT] [WITH TIES] `. `EmitTopRowFilter` generator-renders the expression and uppercases the modifier keywords.

`EmitWrappedList` wraps at `MaxLineLength * 2/3` (= 80 at default 120), measured against the sum of per-element first-line renders plus `", "` separators. Multi-line element renders (e.g. subqueries in SELECT) contribute only their first-line length — the element's own break-to-block handles its vertical layout.

### ScalarSubquery / InPredicate (with subquery) / ExistsPredicate (4b-ii)
Each emits opening `(`, NewLine, `Indent()`, recurses via `subquery.QueryExpression.Accept(this)`, NewLine, dedent, closing `)`. The accept call dispatches to `ExplicitVisit(QuerySpecification)` which opens its own `BeginClauseScope` — so these three overrides do **not** open a scope themselves (doing so would double-wrap and double-indent). For non-`QuerySpecification` subquery shapes (`BinaryQueryExpression` — UNION etc.), `EmitSubqueryQueryExpression` falls through to `EmitFragmentDefault`; proper handling lands in 4b-iv.

`InPredicate.Subquery == null` (values list) falls through to generator. `BooleanComparisonExpression` where one side is a `ScalarSubquery` is handled by `EmitSearchConditionBody`'s top-level dispatcher — the non-subquery side renders inline via generator scaffold (`EmitExpressionScaffold`), the subquery side dispatches through the visitor. AND/OR-mixed search conditions (`BooleanBinaryExpression` containing a subquery) are **not** dispatched — they fall through to generator, so a subquery under AND/OR renders inline. See § Known limitations.

### QuerySpecification fallback retired (4f-ii)
The `QuerySpecRequiresFallback` trip-flag (`TopRowFilter` / `OffsetClause` / `ForClause`) and both its callers (the SelectStatement-level bail at line 100 and the QuerySpec-level synthetic-wrap at line 158) were removed in 4f-ii. Earlier slices kept the fallback because `Sql170ScriptGenerator.GenerateScript(QuerySpecification)` on a *bare* QuerySpec with niche features silently drops trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY) — a ScriptDom quirk. 4f's bundled fix (synthetic-wrap into `SelectStatement`) dodged the drop but introduced an alignment-style leak: the generator with `AlignClauseBodies=true` emits left-aligned-keyword shape ("Style 2"), incompatible with the visitor's right-aligned-keyword staircase ("Style 1"). 4f-ii eliminates the leak by handling TOP / OFFSET / FOR natively in `ExplicitVisit(QuerySpecification)`. 4b-iii-a's earlier removal of `hasJoins` / `hasMultiTableFromClause` had already retired the JOIN-related trip-flags; 4f-ii completes the job.

`EmitGeneratorRaw` survives as a helper for other fallback paths (PIVOT `ForPath`, BBE outside clause scope, INSERT/UPDATE/DELETE `TopRowFilter`, etc.) — it does generator-emit without re-entering `EmitFragmentDefault`'s dispatch.

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
`BinaryQueryExpression` — UNION source stacks correctly). *(4f-iii)* `TopRowFilter` renders
inline between INSERT and INTO/OVER — `INSERT TOP <expr> [PERCENT] [INTO|OVER] <target>` —
via `EmitTopRowFilter`. Trailing `;` not emitted (consistent with SELECT).

### UpdateStatement (4c)
UPDATE / SET / OUTPUT / FROM / WHERE right-align in one clause scope (D1). `maxKw = 6`
(UPDATE / OUTPUT). FROM body reuses `EmitTableReferenceBody` / `EmitWrappedList` — JOINs
stack identically to a SELECT's FROM. SET assignments wrap via `EmitWrappedList` + per-
`SetClause` generator scaffold (subquery-in-NewValue is out of scope for 4c; 4f picks up
scalar-expression overrides that would break the subquery to block inside a SET). WHERE
reuses `EmitSearchConditionBody` — subquery-bearing search conditions break to block the
same way they do in a SELECT's WHERE. *(4f-iii)* `TopRowFilter` renders inline within the
UPDATE clause body — `UPDATE TOP <expr> [PERCENT] <target>` — via `EmitTopRowFilter`. TOP
rides in the body so scope `maxKw` stays at the UPDATE/SET/WHERE width; SET / FROM / WHERE
right-align unaffected.

### DeleteStatement (4c)
Two shapes (D2). Simple form (`FromClause == null`): `DELETE` is the keyword, `FROM
<target>` is the body — keeps scope `maxKw` at 6, so WHERE pads by 1 (col 1). Extended
form (`FromClause != null`, typically with JOINs): `DELETE <target>` and `FROM <join-tree>`
are separate keyword lines inside the scope, right-aligning with WHERE / OUTPUT. Grammar
order: DELETE / (target) → OUTPUT → FROM → WHERE. *(4f-iii)* `TopRowFilter` renders inline
in both shapes: simple form emits `DELETE TOP <expr> FROM <target>` on one body line;
extended form emits `DELETE TOP <expr> <target>` then `FROM <join-tree>` on the next
keyword line. Same `EmitTopRowFilter` reuse as INSERT/UPDATE.

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

### CreateFunctionStatement / AlterFunctionStatement / CreateOrAlterFunctionStatement (4d-iii)

Three thin overrides delegate to `EmitFunctionBody(FunctionStatementBody, keywordPrefix)`.
`FunctionStatementBody` is a sibling of `ProcedureStatementBody` under
`ProcedureStatementBodyBase`, so `Parameters` (same `IList<ProcedureParameter>` type),
`MethodSpecifier`, and `StatementList` are inherited; `EmitProcedureParameterBody` is reused
as-is for parameter rendering. The function-specific declared members are `Name`
(`SchemaObjectName` — note: not `ProcedureReference`), `Options` (`IList<FunctionOption>`),
`OrderHint`, and `ReturnType`.

**Header shape**: `CREATE [OR ALTER]/ALTER FUNCTION <name>` + parameter list +
`RETURNS <return-type>` + optional `OrderHint` line + optional `WITH <opts>` + `AS`.

**Parameter-list shape**: always parenthesised. Empty list emits `()` inline immediately
after the name; non-empty list emits `\n(\n    <param>,\n    <param>\n)` — paren on its own
line, params indented at `+IndentSize`. Functions reject the proc-style no-paren form at
parse time (ScriptDom error #46010 `Incorrect syntax near 'RETURNS'`), so this is
non-negotiable. Always-multi-line (rather than EmitWrappedList's adaptive inline-vs-wrap)
matches the dominant SSMS / GittyExport convention and the only real function in the
corpus (`Sorgu/function store freq.sql`).

**RETURNS shape**: switch on `ReturnType` concrete subclass.
- `ScalarFunctionReturnType { DataType }` → `RETURNS <generator-render(DataType)>`. Scalar
  functions return a single value via `RETURN <expr>` inside a wrapping BEGIN/END.
- `SelectFunctionReturnType { SelectStatement }` → `RETURNS TABLE`. **This is the inline
  TVF case** despite the counterintuitive class name (ScriptDom names the multi-stmt one
  `TableValuedFunctionReturnType`). The body SELECT lives directly on this `ReturnType`,
  not in `StatementList` (which is null for inline TVFs).
- `TableValuedFunctionReturnType { DeclareTableVariableBody }` → `RETURNS @t TABLE (...)`.
  **This is the multi-stmt TVF case.** The `@t TABLE (col defs)` payload is generator-
  rendered as a multi-line block via `_generator.GenerateScript(DeclareTableVariableBody)`.
  Per-column wrap polish (the generator's slightly off `col1 INT         ,` alignment) is
  4d-v territory — proper column-DDL modelling lands with the `CreateTableStatement` slice.
- Unknown subclass → `EmitGeneratorRaw(stmt)` defensively, same pattern as
  `BeginEndAtomicBlockStatement` and `IsMaterialized` views.

**OrderHint**: `OrderBulkInsertOption` for the `WITH ORDER (...)` hint on inline TVFs.
Generator-rendered on its own line when non-null. Rare in practice; corpus may surface
issues to revisit.

**WITH options**: `FunctionOption` items rendered per-option via the generator and
comma-joined on a single `WITH …` line. Same pattern as procs / views. Real options
(`SCHEMABINDING`, `RETURNS NULL ON NULL INPUT`, `CALLED ON NULL INPUT`, `EXECUTE AS …`)
all render correctly through the generator.

**CLR functions**: `MethodSpecifier != null` emits `AS EXTERNAL NAME <spec>` and returns
without a body. Same shape as procs at the matching point in `EmitProcedureBody`.

**Body shape**: branches on `ReturnType`. Inline TVF (`SelectFunctionReturnType`) emits
`RETURN (` + `NewLine` + `Indent()` scope + `EmitFragmentDefault(returnType.SelectStatement)`
+ `AtLineStart` guard + `)` — mirrors `ScalarSubquery` / `ExistsPredicate` break-to-block
exactly. The body SELECT routes through the `SelectStatement` override (clause keywords
right-align, JOINs stack via `QualifiedJoin`, etc.). No trailing `;` inside the parens —
`EmitFragmentDefault` doesn't add one (only `EmitBodyStatements` does, via
`EnsureTrailingSemicolon`).

Scalar and multi-stmt TVF route through `EmitBodyStatements(StatementList)`. ScriptDom
captures the function's outer `BEGIN … END` as a real `BeginEndBlockStatement` at
`StatementList[0]` (always — not implicit), so the existing `BeginEndBlockStatement`
override emits the wrapping naturally. Body content recurses through the visitor
(DECLARE / IF / SELECT / nested control-flow all flow through their respective overrides
with the 4e-ii-b vertical-spacing rule applied).

### CreateTriggerStatement / AlterTriggerStatement / CreateOrAlterTriggerStatement (4d-iv)

Three thin overrides delegate to `EmitTriggerBody(TriggerStatementBody, keywordPrefix)`.
`TriggerStatementBody` is a `TSqlStatement` subclass with no shared base with procs / views /
funcs — the three concrete trigger types are siblings under it. No parameter list (triggers
don't take parameters), so the parameter-rendering machinery from `EmitProcedureBody` /
`EmitFunctionBody` is not reused.

**Header shape** (T-SQL grammar order, source-order observed via probe):

```
CREATE [OR ALTER]/ALTER TRIGGER <name>
    ON <target>
    [WITH <opts>]
    {AFTER | FOR | INSTEAD OF} <event-list>
    [NOT FOR REPLICATION]
AS
<body>
```

Each header section emits at column 0; only the body, when wrapped in BEGIN/END, indents.

**Target rendering** (`TriggerObject`): generator-rendered as a single fragment.
`TriggerObject.TriggerScope` enum (`Normal` / `Database` / `AllServer`) drives the literal
inside the generator — `dbo.t` for Normal (uses the `Name` SchemaObjectName), `DATABASE` for
Database, `ALL SERVER` for AllServer. No per-scope switch in the visitor; the generator
handles the variant correctly. Same shortcut philosophy as `DeclareTableVariableBody` in
`EmitFunctionBody`.

**Event-list rendering** (`TriggerActions[]`): each `TriggerAction` is generator-rendered
individually and comma-joined. Handles all variants uniformly:
- DML actions (`TriggerActionType` = `Insert` / `Update` / `Delete`) render as the action
  keyword.
- DDL actions (`TriggerActionType` = `Event`) carry an `EventTypeGroup` payload — either an
  `EventTypeContainer` (single event like `CREATE_TABLE`) or `EventGroupContainer` (event
  group like `DDL_DATABASE_LEVEL_EVENTS`). Generator handles both.
- Logon trigger (`TriggerActionType` = `LogOn`) renders as `LOGON`.

ScriptDom preserves source order in `TriggerActions[]`, so format mirrors source — no
canonicalization concern with multi-event triggers (`AFTER INSERT, UPDATE, DELETE` stays in
that order).

**Timing keyword** (`TriggerType` enum): mapped via `TriggerTypeKeyword` switch — `After` →
`AFTER`, `InsteadOf` → `INSTEAD OF`, `For` → `FOR`. Default falls to `FOR` defensively.

**WITH options**: same pattern as procs / views / funcs — comma-joined per-option via
generator. `TriggerOption` for `ENCRYPTION`, `ExecuteAsTriggerOption` (subclass) wrapping an
`ExecuteAsClause` for `EXECUTE AS …`.

**NOT FOR REPLICATION**: `IsNotForReplication` bool flag — emit on its own line between
events and `AS` if set.

**Body shape**: mirrors procs (flat list — `StatementList[0]` is the body's first statement
directly), not functions (which always wrap in `BeginEndBlockStatement[0]`). When source has
`AS BEGIN … END`, ScriptDom captures the BEGIN/END as a `BeginEndBlockStatement` and
`EmitBodyStatements` + the existing override emits the wrapping. When source has a single
statement (`AS SELECT 1`), the statement renders flat at column 0.

**WITH APPEND**: parser-rejected by `TSql170Parser` even with `initialQuotedIdentifiers: true`
— the legacy `FOR INSERT WITH APPEND` form fails parse with `Incorrect syntax near 'WITH'`.
Not formattable; dropped from scope and test coverage.

**No corpus**: `Sorgu/` contains zero triggers, so all coverage is synthesized in
`Tests/TriggerFormattingTests.cs`. Exit smoke is paste-into-running-app rather than corpus
canonical-match.

### CreateTableStatement (4d-v)

Single override delegates to `EmitCreateTableBody`. Header: `CREATE TABLE` + generator-rendered
`SchemaObjectName` + `EmitTableDefinitionBody` for the parenthesised body, then optional
`ON <fg>` / `TEXTIMAGE_ON <fg>` / `FILESTREAM_ON <fg>` (each on its own line at column 0), then
optional table-level `WITH (<opts>)` (table options like `MEMORY_OPTIMIZED = ON` and
`SYSTEM_VERSIONING = ON (HISTORY_TABLE = ...)` — generator-rendered per option, comma-joined).

**Body shape**: D2 — no blank-line gap between column block and constraint block. Matches
SSMS canonical and the Sorgu corpus.

```
CREATE TABLE <name>
(
    <col> <type> [IDENTITY(s,i)] [NULL|NOT NULL] [DEFAULT ...] [<col-constraints>],
    ...,
    [CONSTRAINT <name>] PRIMARY KEY [CLUSTERED|NONCLUSTERED] (<cols>)
        [WITH (<index-opts>)]            <-- wrap if oversized; inline if fits
        [ON <filegroup>],
    [INDEX <name> ...],
    [PERIOD FOR SYSTEM_TIME (start, end)]
)
[ON <filegroup>]
[TEXTIMAGE_ON <filegroup>]
[FILESTREAM_ON <filegroup>]
[WITH (<table-opts>)]
```

**Out of scope**: external (`CreateExternalTableStatement` — separate sibling type), graph
(`AsNode` / `AsEdge` flags), ledger (`IsLedger`), `CREATE TYPE ... AS TABLE`. All defensively
fall through to `EmitGeneratorRaw`.

### AlterTableAddTableElementStatement, AlterTableDropTableElementStatement, AlterTableAlterColumnStatement, AlterTableSwitchStatement, AlterTableTriggerModificationStatement, AlterTableConstraintModificationStatement (4d-v)

Six per-subtype overrides. ScriptDom carves ALTER TABLE into six concrete statement types
(no polymorphic body — each is its own ScriptDom statement subclass), so per-type emission is
cleaner than a dispatcher.

**Common shape**: `EmitAlterTableHeader` writes `ALTER TABLE <name>` + NewLine; the action
keyword (`ADD` / `DROP COLUMN|CONSTRAINT` / `ALTER COLUMN` / `SWITCH` / `ENABLE|DISABLE
TRIGGER` / `CHECK|NOCHECK CONSTRAINT`) lands on the next line at column 0 (no indent — the
header and action keyword are visually peers, not parent/child).

**`EmitExistingRowsCheck`**: emits `WITH CHECK ` / `WITH NOCHECK ` prefix on the action line
when `ExistingRowsCheckEnforcement` is set. Used by ADD (the SSMS-scripted FK shape) and
CHECK CONSTRAINT branches.

**ADD multi-element**: `AlterTableAddTableElementStatement.Definition.ColumnDefinitions[] +
TableConstraints[] + Indexes[]` can have multiple entries — single-element ADDs render inline
on the action line; multi-element ADDs route through `EmitTableDefinitionBody` for the
parenthesised, indented block (same shape as CREATE TABLE).

**ADD DEFAULT FOR**: real-corpus shape `ALTER TABLE [t] ADD DEFAULT (expr) FOR [col]` lands as
a `DefaultConstraintDefinition` inside `Definition.TableConstraints[0]` — generator-renders
correctly wholesale (no per-constraint WITH/ON tail to wrap).

**DROP**: `AlterTableDropTableElements[]` carries mixed `Column` / `Constraint` items per
`TableElementType`. First element emits its keyword (`COLUMN` / `CONSTRAINT`); subsequent
elements omit the keyword. Each item also carries `IsIfExists`. Generator-rendered identifiers
preserve quoting.

**ALTER COLUMN**: `AlterTableAlterColumnOption` enum drives the trailer (`NULL`, `NOT NULL`,
`ADD ROWGUIDCOL`, `DROP ROWGUIDCOL`, `ADD PERSISTED`, `DROP PERSISTED`). Optional `Collation`
fragment after the option.

**SWITCH PARTITION**: `SourcePartitionNumber` / `TargetPartitionNumber` are optional integer
literals; emit `SWITCH [PARTITION n] TO <target> [PARTITION n]` on a single line.

**TRIGGER MODIFICATION**: `TriggerEnforcement` enum picks `ENABLE TRIGGER` / `DISABLE TRIGGER`;
`All` flag picks `ALL` vs comma-separated `TriggerNames[]`.

**CONSTRAINT MODIFICATION**: `ConstraintEnforcement` enum picks `CHECK CONSTRAINT` / `NOCHECK
CONSTRAINT`; `All` flag picks `ALL` vs comma-separated `ConstraintNames[]`. `WITH CHECK|NOCHECK`
prefix from `ExistingRowsCheckEnforcement` (independent of `ConstraintEnforcement`).

### EmitTableDefinitionBody (4d-v)

Shared parenthesised-block body. Three call sites: `EmitCreateTableBody`,
`AlterTableAddTableElementStatement` (multi-element ADD), and `EmitFunctionBody`'s
`TableValuedFunctionReturnType` branch (multi-stmt TVF — `RETURNS @t TABLE (cols)` shape).

Shape: NewLine + `(` on its own line at outer indent + `Indent()` block over
`ColumnDefinitions[]`, then `TableConstraints[]`, then `Indexes[]`, then optional
`SystemTimePeriod` (PERIOD FOR SYSTEM_TIME, generator-rendered). Comma-trailing per D3, no
blank-line separator between groups (D2). Closing `)` on its own line at outer indent.

### ColumnDefinition emission (4d-v)

`EmitColumnDefinition` calls `_generator.GenerateScript` per-column. Per-column generator
output is clean — identifier + type + collation + identity + nullable + default + computed-AS
+ inline-constraints + inline-INDEX all render correctly as one line. The column-alignment
padding artifact (`id   INT            NOT NULL,`) only appears when the generator renders a
parent `TableDefinition` wholesale; per-column calls bypass it.

### ConstraintDefinition emission (4d-v)

`EmitConstraintDefinition` dispatches on subtype:

- `UniqueConstraintDefinition` (PK and UQ — distinguished by `IsPrimaryKey` and
  `IndexType.IndexTypeKind`): routes to `EmitUniqueConstraintDefinition` for the WITH-options
  + ON-filegroup wrap logic (D1 option C).
- `ForeignKeyConstraintDefinition` / `CheckConstraintDefinition` /
  `DefaultConstraintDefinition`: no per-constraint WITH/ON tail, generator-rendered wholesale
  as one line. Fits MaxLineLength under realistic naming.

### EmitUniqueConstraintDefinition — D1 option C wrap (4d-v)

The load-bearing wrap helper. Header (`[CONSTRAINT name] PRIMARY KEY|UNIQUE
[CLUSTERED|NONCLUSTERED] (cols ASC|DESC,...)`) is built explicitly to a `StringBuilder`
(constraint identifier + key kind + index-type keyword + column list) so we can measure its
length before deciding inline vs wrap. Why explicit construction rather than generator: the
generator emits the entire constraint as one string with no separation between header and
options/filegroup, leaving no clean point to insert the wrap. Building the header ourselves
keeps that point free.

**Wrap decision**: inline if `currentIndent + headerLength + inlineExtra ≤ MaxLineLength`
where `inlineExtra` accounts for ` WITH (opt1, opt2, ...)` and ` ON <fg>`. Else WITH-options
wrap one-per-line at +2*IndentSize and ON-filegroup trails on its own line at +IndentSize.

**Real-world calibration**: real-corpus PK with full SSMS 6-option WITH block
generator-renders to 215 chars — always wraps. Short UQs with one or two options fit in 116
chars — stay inline. Both shapes co-exist in `Sorgu/create_table.sql` and both lock as
exact-equals tests.

### DeclareVariableStatement (4e-iii)

Drops the generator-injected `AS` keyword (`DECLARE @x AS INT` → `DECLARE @x INT`) — source-faithful, matches SSMS canonical, matches the corpus shapes in `Sorgu/usp_daily_package_info.sql:86` and elsewhere. Each `DeclareVariableElement` rendered through `RenderDeclarationText`: variable name + space + generator-rendered DataType + (optional ` = ` + generator-rendered Value). Single helper used for both wrap measurement and emission.

Wrap shape (A1, settled by corpus): when total declarations + separators exceed `MaxLineLength * 2/3` (same threshold as `EmitWrappedList` / procedure parameters), `DECLARE` sits alone on its line and each declaration lands at +`IndentSize`, comma-trailing — matches the corpus pattern in `Sorgu/usp_daily_package_info.sql:12-15` and `Sorgu/2161.sql:25-39`. Inline form when total fits: `DECLARE @x INT = 5, @y NVARCHAR(50) = 'a', @z BIT`. Trailing `NewLine()` if not already at line start, mirroring INSERT/UPDATE/DELETE statement-end convention.

### DeclareTableVariableStatement (4e-iii)

`DECLARE @t TABLE` header + reuse of `EmitTableDefinitionBody` (4d-v helper) for the parenthesised column / constraint block. Same per-column wrap rules as CREATE TABLE / multi-stmt TVF — fixes the generator's column-alignment padding artifact (`id   INT           ,`) without new code. The body is structurally identical to `TableValuedFunctionReturnType.DeclareTableVariableBody.Definition`, so `EmitTableDefinitionBody` slots in with no adapter.

### RollbackTransactionStatement (4e-iii)

Always emits `ROLLBACK TRANSACTION [name]`. The `Sql170ScriptGenerator` drops the keyword entirely on bare ROLLBACK (`ROLLBACK TRAN;` → `ROLLBACK`) — silent loss of intent, asymmetric with how it handles BEGIN/COMMIT/SAVE TRANSACTION (which it preserves cleanly and we leave generator-rendered). The AST does not preserve the source distinction between `TRAN` and `TRANSACTION`, so always emit the long form (settles A3). Optional savepoint name appended via generator-rendering of `stmt.Name`. Trailing `NewLine()` per statement-end convention.

### Known AND/OR byproduct in ON *(retired in 4f-iv)*
*(historical)* `Sql170ScriptGenerator` renders `BooleanBinaryExpression` (AND / OR) as multi-line by default. Pre-4f-iv, the JOIN ON path generator-rendered the search condition as a raw string and wrote it inline, so embedded newlines from the generator landed at column 1 — the documented col-0 leak visible across the corpus (e.g. `Sorgu/Buyuk Kucuk Kasa Yeni.sql:34`). 4f-iv fixed it by detecting `qj.SearchCondition is BooleanBinaryExpression` and breaking ON to its own line in a synthetic clause scope, so AND/OR right-aligns with ON via the existing `WriteClauseKeyword` machinery. Single-comparison ON keeps the inline-or-long-break heuristic — gate is BBE-specific, no behavior change for non-BBE shapes.

## Known limitations
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

### 4d-iii — 2026-04-25

- **Slice motivation**: `Sorgu/function store freq.sql` (the only real function in the corpus, an `ALTER FUNCTION` scalar with realistic DECLARE / SELECT / IF body) hit `EmitGeneratorRaw` pre-4d-iii. Whole function rendered through `Sql170ScriptGenerator`, so the body SELECTs didn't right-align clause keywords and didn't pick up JOIN stacking / vertical spacing. Visually inconsistent the moment a function sat next to anything else the visitor handled.
- **ScriptDom probe (run before planning)** pinned the structural facts:
  - `FunctionStatementBody : ProcedureStatementBodyBase` — sibling of `ProcedureStatementBody`, not parent-child. Shared base provides `Parameters`, `MethodSpecifier`, `StatementList`. So `EmitProcedureBody` is *not* directly reusable, but the parameter helpers (`EmitProcedureParameterBody` taking `ProcedureParameter`) reuse cleanly.
  - Three `FunctionReturnType` subclasses: `ScalarFunctionReturnType { DataType }` (scalar), `SelectFunctionReturnType { SelectStatement }` (inline TVF — counterintuitive name), `TableValuedFunctionReturnType { DeclareTableVariableBody }` (multi-stmt TVF).
  - For scalar / multi-stmt TVF: `StatementList[0]` is always `BeginEndBlockStatement` (BEGIN/END is captured as a real fragment, not implicit). For inline TVF: `StatementList` is null, the body SELECT lives at `ReturnType.SelectStatement`. This shapes the body-emission branch cleanly.
- **Three function overrides** + shared `EmitFunctionBody(FunctionStatementBody, keywordPrefix)` helper. Same overall shape as `EmitProcedureBody` and `EmitViewBody`. Three new branches in `EmitFragmentDefault` (Create / Alter / CreateOrAlter Function).
- **Parameter-list deviation from procs**: functions REQUIRE parens around the parameter list — even the empty form (`dbo.fn()`). Procs allow no-parens. ScriptDom enforces this at parse time (#46010 `Incorrect syntax near 'RETURNS'`); the first capture pass produced reparse failures across all 10 new tests, surfacing the requirement immediately. Fix: emit `()` inline for the empty case, multi-line `(\n    <param>,\n    ...\n)` with paren on its own line and params at `+IndentSize` for non-empty. Always-multi-line (vs EmitWrappedList's adaptive inline/wrap) matches the dominant SSMS / GittyExport convention and the Sorgu corpus shape; deferred adaptive wrap until corpus surfaces a counter-case.
- **Inline TVF body** (`SelectFunctionReturnType`): `RETURN (` + NL + `Indent()` + `EmitFragmentDefault(returnType.SelectStatement)` + `AtLineStart` guard + `)`. Mirrors `ScalarSubquery` break-to-block exactly. No trailing `;` inside parens (the dispatch path uses `EmitFragmentDefault`, not `EmitBodyStatements`).
- **Scalar / multi-stmt TVF body**: `EmitBodyStatements(stmt.StatementList)` — `StatementList[0]` is the `BeginEndBlockStatement`, the existing override emits BEGIN/END with body recursion happening at `+IndentSize`. No new code needed for the wrapping; everything was already in place from 4e-i + 4e-ii-b.
- **Multi-stmt TVF column DDL**: generator-renders the whole `DeclareTableVariableBody` as a single multi-line block (`@t TABLE (\n    col1 INT         ,\n    col2 VARCHAR (10))`). The off-alignment (`INT         ,`) is a generator artifact; per-column polish is **4d-v territory** — the proper fix is a real `CreateTableStatement`/column-DDL slice that handles CTE / view / TVF column lists with consistent wrap rules.
- **OrderHint**: generator-renders on its own line when non-null. None of the corpus functions exercise this; defensive emission for completeness.
- **CLR functions** (`MethodSpecifier != null`): emit `AS EXTERNAL NAME <spec>` and return. Mirrors procs.
- **Tests**: 10 new facts in `Tests/FunctionFormattingTests.cs` (capture-and-lock; no `Assert.True(true)` left in committed file). Minimal scalar / short params / long params / realistic body (DECLARE + IF + SELECT + RETURN, exercises body recursion + 4e-ii-b vertical spacing) / inline TVF minimal / inline TVF with JOIN+WHERE / multi-stmt TVF / ALTER / CREATE OR ALTER / WITH SCHEMABINDING. All exact-equals + `Assert.NotNull(ReParse(output))`. Full suite **148 → 158**, all pass. Harness **51 / 0 / 2** unchanged.
- **Real-function smoke**: `Sorgu/function store freq.sql` formats and re-parses cleanly (errors: 0). The function header lands as expected; body SELECTs right-align clause keywords; JOINs stack; DECLARE clusters stay tight while block-level statements get blank-line separation. Two pre-existing Known Limitations show but are not 4d-iii regressions: (1) the inner subquery with `TOP 2` falls back to generator and drops trailing `WHERE` / `GROUP BY` clauses (4b-iii bare-QuerySpec quirk); (2) the chained `IF / ELSE IF / ELSE IF` stairsteps progressively (4e-ii Known Limitation).
- **Out of scope / deferred**: per-column DDL wrap for multi-stmt TVF (joint with `CreateTableStatement` in 4d-v); subquery-with-TOP trailing-clause drop fix (separate 4b-iii revisit); ELSE IF flatten (4e-ii Known Limitation has a sketch); CLR function smoke (no corpus example).

### 4d-iv — 2026-04-25

- **Slice motivation**: triggers had zero coverage in the visitor — `CreateTriggerStatement` / `AlterTriggerStatement` / `CreateOrAlterTriggerStatement` all routed to `EmitGeneratorRaw`. Bodies (commonly BEGIN/END blocks with DECLARE / IF / UPDATE) rendered through `Sql170ScriptGenerator` so clause-keyword right alignment, JOIN stacking, and 4e-ii-b vertical spacing didn't fire inside them. Same visual inconsistency that motivated 4d-iii (functions).
- **No real corpus**: `Sorgu/` contains zero triggers. Whole slice driven from synthesized inputs — 11 probe shapes (DML AFTER / multi-event / INSTEAD OF / WITH options + NOT FOR REPLICATION / realistic body / DDL ON DATABASE / DDL ON ALL SERVER / ALTER / CREATE OR ALTER / WITH APPEND / logon). 10 of 11 parse cleanly under `TSql170Parser`; `WITH APPEND` is parser-rejected and dropped from scope.
- **ScriptDom probe (run before planning)** pinned the structural facts:
  - `Create/Alter/CreateOrAlterTriggerStatement : TriggerStatementBody` — three siblings under a shared base (mirrors proc / view / func pattern). Single `EmitTriggerBody` helper applies.
  - `TriggerStatementBody` carries: `Name` (SchemaObjectName), `TriggerType` (After / InsteadOf / For), `TriggerObject` (with `TriggerScope` enum: Normal / Database / AllServer; `Name` only present for Normal), `Options[]` (`TriggerOption` for `ENCRYPTION`, `ExecuteAsTriggerOption` for `EXECUTE AS …`), `TriggerActions[]` (each carries `TriggerActionType`: Insert / Update / Delete / Event / LogOn — Event variant has `EventTypeGroup` = `EventTypeContainer` for single events or `EventGroupContainer` for groups), `IsNotForReplication` (bool), `StatementList` (flat — like procs, NOT wrapped in implicit BeginEndBlockStatement like functions).
  - No parameter list — triggers don't have parameters.
- **Three trigger overrides** + shared `EmitTriggerBody(TriggerStatementBody, keywordPrefix)` helper. Three new branches in `EmitFragmentDefault` (Create / Alter / CreateOrAlter Trigger). Header emission order (T-SQL grammar, source-order observed): keyword + Name + `ON ` + TriggerObject + optional `WITH <opts>` + timing keyword + comma-joined event list + optional `NOT FOR REPLICATION` + `AS` + body.
- **Generator-render shortcut for TriggerObject and TriggerActions[]**: rather than switch on `TriggerScope` to emit `dbo.t` / `DATABASE` / `ALL SERVER` literals manually, and rather than switch on `TriggerActionType` × `EventTypeGroup` shape to emit DML keywords vs event names vs event groups, both go through `_generator.GenerateScript` per fragment. ScriptDom's generator handles every variant correctly with no per-shape branching needed. Capture-and-lock confirmed clean output across DML, DDL-database, DDL-all-server, and logon shapes. If a future corpus surfaces an off-aesthetic generator output, the fallback is per-variant rendering — but the capture pass produced no such case.
- **Body shape mirrors procs**: `StatementList` is a flat list — `StatementList[0]` is directly the body's first statement when source has a single-statement form (`AS SELECT 1`), or a captured `BeginEndBlockStatement` when source has `BEGIN ... END`. `EmitBodyStatements` + the existing `BeginEndBlockStatement` override handles both shapes naturally; vertical-spacing rule (4e-ii-b) applies inside BEGIN/END bodies (DECLARE clusters tight, block-level statements separated by blank lines) — exercised in the realistic-body test.
- **Tests**: 10 new facts in `Tests/TriggerFormattingTests.cs` (capture-and-lock; no `Assert.True(true)` left in committed file). Minimal AFTER INSERT / multi-event / INSTEAD OF / WITH options + NOT FOR REPLICATION / realistic body (DECLARE + IF EXISTS subquery + UPDATE) / DDL ON DATABASE / DDL ON ALL SERVER event group / ALTER / CREATE OR ALTER / logon trigger. All exact-equals + `Assert.NotNull(ReParse(output))`. Full suite **158 → 168**, all pass. Harness **51 / 0 / 2** unchanged.
- **Out of scope / deferred**: `WITH APPEND` (parser-rejected — not formattable); 4d-v (TABLE).

### 4d-v — 2026-04-25

- **Slice motivation**: tables had zero coverage in the visitor. `CreateTableStatement` and the six `AlterTable*Statement` subtypes all routed to `EmitGeneratorRaw`. Generator output for a real-corpus PK with the SSMS 6-option WITH block produces a 215-character single line (way past MaxLineLength 120); per-column rendering inside a `TableDefinition` introduces alignment-padding artifacts (`id   INT            NOT NULL,`); ALTER TABLE statements landed at column 0 with no separation between header and action keyword. None of those were acceptable for users working with real DDL.
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_TableProbe.cs` walked the AST for 26 inputs spanning every CREATE / ALTER TABLE shape. Pinned: six concrete `AlterTable*Statement` subtypes (no polymorphic body — each its own ScriptDom statement type); `ConstraintDefinition` hierarchy (`Nullable` / `Default` / `Unique` (PK & UQ unified, distinguished by `IsPrimaryKey`) / `ForeignKey` / `Check`); `UniqueConstraintDefinition` carries `IndexOptions[]`, `OnFileGroupOrPartitionScheme`, `IndexType`, `Columns: ColumnWithSortOrder[]`; `CreateTableStatement` carries `OnFileGroupOrPartitionScheme`, `TextImageOn`, `FileStreamOn`, `Options[]` (table-level — `MemoryOptimizedTableOption`, `SystemVersioningTableOption`, etc.). Probe also rendered each fragment via the generator to size up the inline-vs-wrap decision concretely (full PK = 215 chars; corpus PK with 2 options = 116 chars).
- **Three open decisions, all signed off before coding**:
  - **D1 — Constraint WITH-options inline vs wrap.** Option C (conditional wrap on overflow) chosen — same philosophy as `EmitWrappedList` already used for SELECT / GROUP BY / ORDER BY. Inline if `currentIndent + headerLength + inlineExtra ≤ MaxLineLength`; else WITH-options wrap one-per-line at +2*IndentSize and ON-filegroup trails on its own line at +IndentSize. Rejected option A (always-inline — blows past 120 on every realistic SSMS-scripted PK) and option B (always-wrap — vertical-spreads single-option cases unnecessarily).
  - **D2 — No blank-line gap between column block and constraint block.** Matches SSMS canonical and the Sorgu corpus. Tight wrap inside the `( ... )` paren block.
  - **D3 — Multi-stmt TVF column-list backfill in this slice.** `EmitFunctionBody`'s `TableValuedFunctionReturnType` branch was generator-fallback in 4d-iii (squashed columns and trailing-paren on last column line — `col1 INT         ,\n    col2 VARCHAR (10))`). Now reuses `EmitTableDefinitionBody` for clean per-column wrap matching CREATE TABLE shape. `Tests/FunctionFormattingTests.cs::Format_CreateFunction_MultiStatementTvf` expected updated.
- **One CreateTable override + six AlterTable overrides** + four shared helpers (`EmitTableDefinitionBody`, `EmitColumnDefinition`, `EmitConstraintDefinition`, `EmitUniqueConstraintDefinition`) + two thin helpers (`EmitAlterTableHeader`, `EmitExistingRowsCheck`). ALTER family enumerated by probe — six concrete subtypes: `AlterTableAddTableElementStatement` (covers ADD column / CONSTRAINT / DEFAULT FOR / WITH CHECK ADD), `AlterTableDropTableElementStatement` (DROP COLUMN / CONSTRAINT, mixable per-element via `TableElementType`), `AlterTableAlterColumnStatement`, `AlterTableSwitchStatement`, `AlterTableTriggerModificationStatement` (ENABLE / DISABLE TRIGGER), `AlterTableConstraintModificationStatement` (CHECK / NOCHECK CONSTRAINT). Per-subtype overrides cleaner than a polymorphic dispatcher because shapes diverge (no shared statement member set).
- **Wired all seven new statement types into `EmitFragmentDefault`** dispatch. (Bug surfaced in capture pass: missing dispatch entries caused the visitor's overrides to never fire — output came from `EmitGeneratorRaw` fallback. Easy fix once spotted.)
- **Tests**: 14 new in `Tests/TableFormattingTests.cs` + 9 new in `Tests/AlterTableFormattingTests.cs` (capture-and-lock; all exact-equals + `Assert.NotNull(ReParse(output))`; no `Assert.True(true)` left in committed files). One backfill: `Format_CreateFunction_MultiStatementTvf` expected updated to clean column-list output. Full suite **168 → 191**, all pass. Harness **51 / 0 / 2** unchanged (canonical-match check is AST-equivalence via round-trip, so formatting changes preserve it).
- **Sorgu visual smoke**: `Sorgu/create_table.sql`, `Sorgu/tablo_yarat.sql`, `Sorgu/[dbo].[t_xml_exp_ivc_detail].sql` all format and re-parse cleanly with `LOOKOUT_USE_NEW_FORMATTER=1`.
- **Out of scope / deferred**: `CreateExternalTableStatement` (separate sibling type — Polybase / Synapse external tables); graph tables (`AsNode` / `AsEdge`); ledger tables (`IsLedger`); `CREATE TYPE ... AS TABLE` (separate `CreateTypeTableStatement` type). All defensively fall through to `EmitGeneratorRaw`. CTE column-list promotion to `EmitWrappedList` not needed in this slice — corpus surfaced no long CTE column lists.
- **4d-v completes the major DDL-object rectangle** (proc / view / func / trigger / table). Next: 4e-iii (DECLARE / SET / RETURN / transaction control — taste refinements only); 4f (PIVOT / APPLY / ParenthesisExpression); 4g (comments). Natural release point at 4d-v end for v3.1.0 formatter-rollup.

### 4e-iii — 2026-04-25

- **Slice motivation**: small-fry body-statement types had no overrides — `DeclareStatement` family, `SetStatement` family, `ReturnStatement`, transaction control, `ThrowStatement`, `RaiseErrorStatement`. Original handoff predicted "taste refinements only — generator fallback emits these correctly today." Reality after probing: 4 of the 13 types had real bugs (3 overrides land them); 8 were already clean and got skipped.
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_StatementProbe.cs` (temp, removed after) walked the AST + raw generator output for 21 inputs spanning every shape on the 4e-iii list, plus `Tests/TEMP_FormatterProbe.cs` for end-to-end visitor output on the same inputs. Pinned: `DeclareVariableStatement` is one statement with `Declarations[]` (not N statements); `DeclareTableVariableStatement.Body.Definition` is a `TableDefinition` (same shape `EmitTableDefinitionBody` already consumes); generator emits `AS` for declarations; generator drops the `TRANSACTION` keyword on bare `ROLLBACK` (`ROLLBACK TRAN;` → `ROLLBACK`); generator canonicalizes `BEGIN TRAN` / `COMMIT TRAN` / `SAVE TRAN` to the long form (settles A3 — source-preservation isn't on the table without bypassing the generator entirely for those types); generator's column-padding artifact appears in `DECLARE @t TABLE` output (same root cause as the 4d-iii multi-stmt TVF artifact).
- **Five aesthetic decisions, all signed off before coding**:
  - **A1 — DECLARE multi-var wrap shape: shape #2** (DECLARE alone on header line, vars at +`IndentSize`, comma-trailing). Corpus-matching (`Sorgu/usp_daily_package_info.sql:12-15`, `Sorgu/2161.sql:25-39`); avoids the fragile column-alignment-to-keyword-width game that shape #1 would have required. Wrap math: same `MaxLineLength * 2/3` threshold as `EmitWrappedList` and procedure parameters (A4 — one wrapping rule, not two).
  - **A2 — drop generator-injected `AS`** in `DECLARE @x AS INT`. Source-faithful + corpus-matching + SSMS-canonical. Generator artifact, not source.
  - **A3 — always emit `ROLLBACK TRANSACTION`**. Symmetric with BEGIN / COMMIT / SAVE TRANSACTION. Losing a keyword silently is a bug, not a style choice. AST does not preserve the source `TRAN` vs `TRANSACTION` distinction, so all-or-nothing.
  - **A4 — wrap math same as procedure parameters.** `MaxLineLength * 2/3` threshold. One rule.
  - **A5 — `SetVariableStatement` subquery RHS deferred to 4f.** Right boundary: `SET @x = (subquery)` uses generator's left-aligned-keyword style internally. Real visual inconsistency, but resolution is a scalar-expression-level concern (4f territory — arbitrary `ScalarExpression` break-to-block), not a statement-level one. Documented as a known carry-over.
- **Three overrides** + dispatch wiring. `ExplicitVisit(DeclareVariableStatement)` (drops AS, wraps multi-var per A1, comma-trailing); `ExplicitVisit(DeclareTableVariableStatement)` (reuses `EmitTableDefinitionBody` directly — body shape is structurally identical to multi-stmt TVF); `ExplicitVisit(RollbackTransactionStatement)` (always emits `ROLLBACK TRANSACTION [name]`). All three append `if (!_emitter.AtLineStart) _emitter.NewLine()` at end per statement-end convention (mirrors INSERT / UPDATE / DELETE) — `;` enforcement comes from `EnsureTrailingSemicolon` only inside body recursion, not at TSqlBatch level.
- **Dispatch wiring** in `EmitFragmentDefault` (the 4d-v silent-fallthrough trap from last slice — overrides without dispatch entries route to `EmitGeneratorRaw` and never fire). Three new branches added.
- **Plan deviation flagged in chat** (memory `feedback_plan_deviation_transparency`): handoff scope was 13 statement types; probe shrank it to 3 overrides. Surfaced in plan-phase before coding, with the 8 generator-clean types listed explicitly.
- **Tests**: 8 new in `Tests/StatementFormattingTests.cs` (capture-and-lock; all exact-equals + `Assert.NotNull(ReParse(output))`; no `Assert.True(true)` per memory `feedback_capture_test_labels`). 8 existing tests asserting `DECLARE @x AS INT` form across `BlockStatementTests` / `FunctionFormattingTests` / `TriggerFormattingTests` updated to drop `AS`; 3 existing `ROLLBACK;` assertions across `TriggerFormattingTests` / `BlockStatementTests` updated to `ROLLBACK TRANSACTION;`. Full suite **191 → 199**, all pass. Harness **51 / 0 / 2** unchanged.
- **Bundled fix — SelectStatement niche-feature recursion (pre-existing 4b-i bug, surfaced by 4e-iii smoke)**: `Sorgu/2161.sql`'s `SELECT * INTO #weight FROM (...)` crashed the app with a stack overflow. The `SelectStatement` override's tail-fallback for niche features (`Into` / `On` / `ComputeClauses` / `OptimizerHints`) called `EmitFragmentDefault(statement)` which re-routed back into `ExplicitVisit(SelectStatement)` — infinite recursion. Two bugs in one site: (a) wrong dispatch (should have been `EmitGeneratorRaw` to bypass the routing, mirroring the `QuerySpecRequiresFallback` bail at line 100); (b) wrong placement (the bail ran *after* the visitor had already emitted the QuerySpec output, so even with `EmitGeneratorRaw` the path would double-emit — generator output stacked on top of visitor output). Fix: move the niche-feature trip-flag check above the visitor emission, mirror the `QuerySpecRequiresFallback` shape exactly. Latent since 4b-i; never surfaced because no `SELECT INTO` / `OPTIMIZER HINTS` / etc. in the harness corpus and no test in the suite. Regression test added (`Format_SelectInto_FallsBackToGeneratorWithoutCrash`) — round-trip + Assert.Contains keyword survival, light shape so it doesn't lock the generator's exact output. Suite **199 → 200**.
- **Sorgu visual smoke**: `Sorgu/usp_daily_package_info.sql` (DECLARE-cluster-heavy), `Sorgu/2161.sql` (mixed control flow with multi-var DECLARE) format and re-parse cleanly with `LOOKOUT_USE_NEW_FORMATTER=1`.
- **Out of scope / deferred**: `SetVariableStatement` subquery RHS still uses generator's left-aligned style internally (A5 — defer to 4f); `RAISERROR (...) WITH NOWAIT` puts `WITH NOWAIT` on a new line at +`IndentSize` (generator default — left as-is per probe).
- **8 statement types intentionally not overridden** (probe showed generator output is clean): `DeclareCursorStatement`, `SetVariableStatement` (without subquery), `PredicateSetStatement` (SET NOCOUNT etc.), `SetTransactionIsolationLevelStatement`, `ReturnStatement`, `BeginTransactionStatement`, `CommitTransactionStatement`, `SaveTransactionStatement`, `ThrowStatement`, `RaiseErrorStatement`. Recorded here because future slices may revisit any of them.

### 4f — 2026-04-25

- **Slice motivation**: PIVOT IN-list overflow (no wrap rule, runs past `MaxLineLength`); `SetVariableStatement` subquery RHS uses generator's left-aligned style (A5 carry-over from 4e-iii); APPLY / ParenthesisExpression / "remaining expression types" predicted as needing work.
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_FourFProbe.cs` (temp, removed after) walked the AST + raw generator output + current formatter output for PIVOT (short / long IN-list / subquery source / corpus shape), UNPIVOT, OUTER APPLY (function and subquery RHS), CROSS APPLY corpus shape, nested `ParenthesisExpression`, `SetVariableStatement` (scalar / subquery / `+=`), block-form re-parse safety, BETWEEN, multi-AND in ON. Pinned: `PivotedTableReference` AST shape (`AggregateFunctionIdentifier` MultiPartIdentifier, `ValueColumns` IList<ColumnReferenceExpression>, `PivotColumn`, `InColumns` IList<Identifier>, `Alias`, `ForPath`); `UnpivotedTableReference` mirror with singular `ValueColumn` (Identifier) and `ColumnReferenceExpression` `InColumns`; `OUTER APPLY dbo.fn(x.col)` routes through `UnqualifiedJoin` with `SchemaObjectFunctionTableReference` on RHS — already correct via 4b-iii-a's default branch; `((1+2)*3)` round-trips through generator unchanged; `SET @x =\n    (SELECT ...)` re-parses with 0 errors (block-form is safe).
- **Three aesthetic decisions, all signed off before coding**:
  - **A1 — PIVOT IN-list wrap shape**: tight wrap (only the IN-list breaks, not the whole PIVOT clause). Header (`PIVOT (agg(val) FOR col IN (`) on the FROM body line; values one-per-line at +`IndentSize`; closing `))` and ` AS alias` return to body column. Single-pattern shape — UNPIVOT mirrors.
  - **A2 — wrap threshold full `MaxLineLength`**: not the `MaxLineLength * 2/3` used by `EmitWrappedList` — that threshold is for SELECT-list items behind a right-aligned keyword block; PIVOT's IN-list lives in the FROM body where full width is available.
  - **A3 — SET @x = (subquery) break style**: `(` on its own line, body indented, `)` on its own line. Mirrors 4b-ii's `ScalarSubquery` exactly — single break-to-block pattern across the formatter, not two.
- **Three overrides**: `ExplicitVisit(PivotedTableReference)` (inline-or-wrap with full-`MaxLineLength` measurement; `ForPath` defensive fallback); `ExplicitVisit(UnpivotedTableReference)` (mirror of PIVOT); `ExplicitVisit(SetVariableStatement)` (only `Expression is ScalarSubquery` triggers; everything else generator-passes-through). Three new helpers: `JoinPart` (single-fragment generator-render-and-trim), `RenderEach<T>` (list of fragments → list of trimmed strings), `JoinScalars<T>` (`RenderEach` + `string.Join(", ", ...)`); `AssignmentOperator` switch covers all 9 `AssignmentKind` enum values.
- **Dispatch wiring**: `EmitTableReferenceBody` (PivotedTableReference / UnpivotedTableReference branches); `EmitFragmentDefault` (PivotedTableReference / UnpivotedTableReference / SetVariableStatement branches). The 4d-v / 4e-iii silent-fallthrough trap remembered.
- **Plan deviation flagged in chat** (memory `feedback_plan_deviation_transparency`): handoff scope was "PIVOT / APPLY / ParenthesisExpression / remaining expression types"; probe confirmed APPLY-with-function and ParenthesisExpression already work correctly through existing paths, shrinking scope to PIVOT + UNPIVOT + SetVariable. APPLY and ParenthesisExpression land as regression tests rather than overrides.
- **Bundled fix — `QuerySpecification` niche-feature fallback (pre-existing 4b-ii bug, surfaced by 4f's SetVariable subquery-RHS override)**: bare-QuerySpec generator drops trailing clauses (WHERE / GROUP BY / HAVING / ORDER BY) when niche features (TOP / OFFSET / FOR) are present — same root cause as the documented bare-QuerySpec quirk. Top-level `SelectStatement`'s niche-feature fallback already wraps to avoid this; the subquery path (`ScalarSubquery` → `EmitSubqueryQueryExpression` → `QuerySpecification`) didn't. Latent since 4b-ii; never surfaced because no test or harness file had a ScalarSubquery containing both a niche feature and a trailing clause until `SET @x = (SELECT TOP 1 id FROM t WHERE col = 'a')` was probed. Fix: replace `EmitFragmentDefault(q)` in the niche-fallback with `EmitGeneratorRaw(new SelectStatement { QueryExpression = q })` — synthetic-wrap, narrow blast radius, no API change. Regression test added (`Format_SetVariable_SubqueryRhs_WithTop_PreservesAllClauses`).
- **Tests**: 7 new in `Tests/PivotFormattingTests.cs` (capture-and-lock for inline / wrap / UNPIVOT / corpus subquery source / round-trip idempotence; regression-only for OUTER APPLY function and nested parenthesis); 4 new in `Tests/StatementFormattingTests.cs` (SET scalar / `+=` / subquery / subquery+TOP). All exact-equals + `Assert.NotNull(ReParse(output))` per memory `feedback_capture_test_labels`. Suite **200 → 211**, all pass. Harness **51 / 0 / 2** unchanged.
- **Sorgu visual smoke**: `Sorgu/usp_crate_fulfillment_1943616.sql` (PIVOT), `Sorgu/executionhistory.sql` (CROSS APPLY function), `Sorgu/lock_Session.sql` (OUTER APPLY function in a JOIN chain) format and re-parse cleanly with `LOOKOUT_USE_NEW_FORMATTER=1`.
- **Out of scope / deferred**: `BooleanBinaryExpression` outside a clause scope — the AND/OR-at-col-1 quirk in ON / CASE-WHEN / scalar contexts. Probe confirmed it's structurally distinct from "expression types" (it's a scope/dispatch concern, not a missing override). Continues as a Known limitation; best home is 4g or a separate slice. PIVOT FOR PATH (graph SQL) and UNPIVOT FOR PATH defensively fall through to the generator — out of scope this slice.
- **Two corpus shapes intentionally not overridden** (probe showed existing paths handle them correctly): `SchemaObjectFunctionTableReference` on the RHS of `CROSS APPLY` / `OUTER APPLY` (4b-iii-a's `EmitTableReferenceBody` default branch produces clean output); nested `ParenthesisExpression` like `((1+2)*3)` (generator round-trips unchanged — AST shape is load-bearing for arithmetic grouping, no flattening). Recorded here because future slices may revisit.

### 4f-ii — 2026-04-25

- **Slice motivation**: 4f's bundled fix (synthetic-wrap into `SelectStatement` for niche-feature subqueries) routed inner QuerySpec output through `Sql170ScriptGenerator` with `AlignClauseBodies=true`, which produces left-aligned-keyword shape ("Style 2"). The visitor's native `BeginClauseScope` machinery produces right-aligned-keyword staircase ("Style 1"). Mixing both in one query (e.g. PIVOT outer + `SET @x = (SELECT TOP 1 …)` inner — Ömer's screenshot) is visually jarring. Originally accepted as "oh well" at 4f end; reopened when it became the most visible bug left.
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_FourFiiProbe.cs` walked AST shapes for `TopRowFilter` / `OffsetClause` / `XmlForClause` / `JsonForClause` / `BrowseForClause` / `ReadOnlyForClause` / `UpdateForClause`, generator output for 17 SQL shapes (TOP int / TOP paren / TOP percent+ties / TOP var / OFFSET only / OFFSET+FETCH / FOR XML PATH('') / FOR XML AUTO / FOR JSON / FOR BROWSE / STUFF FOR XML / SELECT TOP in WHERE / SET TOP / nested QDT / etc.), then formatter output (raw lead-counts) across 16 nesting contexts (top-level / proc body / CTE body / INSERT / MERGE-USING / VIEW / nested QDT / old-style implicit join). Pinned: `TopRowFilter.Expression : ScalarExpression`, `Percent`, `WithTies`; `OffsetClause.OffsetExpression : ScalarExpression`, `FetchExpression : ScalarExpression?` (FETCH optional); `OrderByClause` / `OffsetClause` / `ForClause` inherited from `QueryExpression`, `TopRowFilter` direct on `QuerySpecification`. Probe also confirmed: pure-visitor cases (QDT-without-TOP, CTE-with-JOIN, etc.) already render staircase-correctly across all 16 contexts; the leak is specific to the niche-feature fallback path.
- **Probe correction** (memory `feedback_pushback_when_objections_are_weak`): initial framing missed the bug — claimed "AlignClauseBodies fixes everything" based on visitor-without-niche-feature cases. Ömer pushed back with the screenshot. Re-probe found `LEFT JOIN (SELECT TOP 5 ...) alias`, `SET @x = (SELECT TOP 1 ...)`, `SELECT (SELECT TOP 1 ...) FROM t` all leak Style 2. Updated recommendation from tight-scope (only OFFSET) to full-scope (all of TOP / OFFSET / FOR).
- **Three aesthetic decisions, all signed off before coding**:
  - **A1 — TOP placement**: inline within the SELECT clause body, after `[ALL|DISTINCT]` and before SelectElements. Form: `TOP <expr> [PERCENT] [WITH TIES] `. Mirrors `UniqueRowFilter` placement; matches generator's canonical and SSMS shape.
  - **A2 — OFFSET shape**: own clause-keyword line after ORDER BY. Body holds `OFFSET <expr> ROWS [FETCH NEXT <expr> ROWS ONLY]` inline — splitting earns nothing for the corpus (zero OFFSET usages) and SSMS canonical keeps tight.
  - **A3 — FOR clause body**: own clause-keyword line after OFFSET. Body via prefix-strip of generator-rendered `ForClause` (strip `"FOR "`, emit the rest). `XmlForClauseOption` / `JsonForClauseOption` enum-and-literal rendering is non-trivial; the generator handles it correctly. Trade: inherits the generator's space-before-paren generator-ism (`PATH ('')`) — recorded as a known limitation, fixable in 4g if anyone cares.
- **Three helpers, no overrides**: `EmitTopRowFilter` (inline TOP body); `EmitOffsetClauseBody` (OFFSET body, FETCH inline); `EmitForClauseBody` (prefix-strip generator render). Wired into `ExplicitVisit(QuerySpecification)` directly. No new dispatch entries in `EmitFragmentDefault` — these are clauses, not statements; the 4d-v / 4e-iii / 4f silent-fallthrough trap doesn't apply.
- **Removals**: `QuerySpecRequiresFallback` deleted entirely; SelectStatement-level fallback at line 100 deleted; QuerySpec-level synthetic-wrap fallback at line 158 deleted. The niche-feature trip-flag is gone — `EmitGeneratorRaw` survives only for unrelated paths (PIVOT `ForPath`, BBE outside clause scope, INSERT/UPDATE/DELETE TopRowFilter, etc.).
- **Plan deviation flagged in chat** (memory `feedback_plan_deviation_transparency`): plan's "re-bake corpus baselines" step turned out unnecessary — the harness compares canonical AST forms via `Sql170ScriptGenerator` round-trip, not byte-level output. Aesthetic-only changes pass through silently. Harness stayed **51 / 0 / 2** with no intervention.
- **Tests**: 9 new in `Tests/QuerySpecificationFormattingTests.cs` (TOP-int / TOP-paren / TOP-percent+ties / TOP-var / OFFSET-only / OFFSET+FETCH / FOR XML PATH('') / FOR JSON / FOR BROWSE) — exact-equals capture-and-lock. 1 new in `Tests/StatementFormattingTests.cs` (`Format_ScalarSubquery_WithInnerTop_Staircase`); the existing `Format_SetVariable_SubqueryRhs_WithTop_PreservesAllClauses` (Assert.Contains shape-light) replaced with `Format_SetVariable_SubqueryRhsTop_Staircase` (exact-equals on the new staircase shape). 1 new in `Tests/JoinAndCteFormattingTests.cs` (`Format_QdtInJoin_WithInnerTop_Staircase`). 1 captures-known-limitation in `Tests/QuerySpecificationFormattingTests.cs` (`Format_StuffForXmlPath_Corpus_KnownGeneratorPassthrough` — function-arg subquery still renders Style 2 because the function call is generator-rendered as one expression; out of scope this slice). Suite **211 → 223**, all pass. Harness **51 / 0 / 2** unchanged.
- **Sorgu visual smoke**: the screenshot's PIVOT + `SET @x = (SELECT TOP 1 …)` query format with consistent staircase across both blocks; `Sorgu/string_agg.sql` (function-arg STUFF FOR XML PATH) renders unchanged from pre-4f-ii (captured as known limitation).
- **Out of scope / deferred**: INSERT / UPDATE / DELETE `TopRowFilter` trip-fallbacks at TSqlFormatterVisitor.cs:812 / 857 / 899 — same Style-2 leak when `UPDATE TOP (N) ...` etc. is used, but on `InsertSpecification` / `UpdateSpecification` / `DeleteSpecification` (separate code paths). Carry-over to 4f-iii or bundled with the BBE-quirk slice. Function-arg `ScalarSubquery` (e.g. `STUFF((SELECT ...))` corpus pattern) — function call generator-rendered as one expression; inner subquery never reaches visitor dispatch. Separate slice. Generator-ism `PATH ('')` space-before-paren — minor, fixable in 4g comment slice if it bothers anyone.

### 4f-iii — 2026-04-25

- **Slice motivation**: 4f-ii retired the `QuerySpecification` niche-feature trip-flag and eliminated the Style-2 leak for the SELECT-side. Three trip-fallbacks on the DML side (TSqlFormatterVisitor.cs:824 INSERT, :869 UPDATE, :911 DELETE) — `if (spec.TopRowFilter != null) { EmitGeneratorRaw(stmt); return; }` — were explicitly deferred from 4f-ii as the planned 4f-iii or BBE-quirk-bundled slice. Branch B in the post-4f-ii planning conversation: "tiny, mechanical, builds momentum, reuses `EmitTopRowFilter` from 4f-ii without modification."
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_FourFiiiProbe.cs` — disposable, deleted at slice end. Walked AST shape for each spec's `TopRowFilter` (all three are `TopRowFilter` type — helper reusable as-is, no per-spec branching needed); generator output for 10 shape variations (INSERT TOP basic / INSERT TOP PERCENT / INSERT TOP from SELECT / INSERT TOP w/ OUTPUT / UPDATE TOP basic / UPDATE TOP PERCENT / UPDATE TOP w/ JOIN / DELETE TOP simple / DELETE TOP PERCENT simple / DELETE TOP extended w/ JOIN); and current formatter output (confirmed Style-2 leak still firing pre-fix). Corpus search: zero `INSERT TOP` / `UPDATE TOP` / `DELETE TOP` instances across `Sorgu/*.sql` (264 files) — synthetic-input only.
- **Three placement decisions, signed off before coding**:
  - **B1 — INSERT placement**: TOP between INSERT and INTO/OVER. INSERT has no clause scope (D3 from 4c). Form: `INSERT TOP <expr> [PERCENT] INTO <target>`. `EmitTopRowFilter` writes its own trailing space; INTO/OVER follows directly without a leading space; one trailing space before target.
  - **B2 — UPDATE placement**: TOP rides in body of UPDATE keyword line (inside the existing UPDATE/SET/FROM/WHERE clause scope). Same pattern as 4f-ii's QuerySpec TOP-in-SELECT — TOP-in-body keeps scope `maxKw` at UPDATE width (6), so SET/FROM/WHERE right-align unaffected.
  - **B3 — DELETE placement, both shapes**: simple form (`spec.FromClause == null`) emits `DELETE TOP <expr> FROM <target>` on one body line — TOP rides in body before the existing-in-body `FROM ` keyword. Extended form (with explicit FromClause) emits `DELETE TOP <expr> <target>` on the DELETE keyword line; FROM continues on its own keyword line. Same body-not-keyword placement keeps scope `maxKw` at DELETE width.
- **No new helpers**: `EmitTopRowFilter` (4f-ii, line 191) reused unchanged across all three call sites. `_emitter.Write("TOP ")` then expression then `[PERCENT]?` then `[WITH TIES]?` then trailing space.
- **No new dispatch entries**: INSERT / UPDATE / DELETE were already statement-level overrides registered in `EmitFragmentDefault` (lines 2330–2332). The 4d-v / 4e-iii / 4f silent-fallthrough trap doesn't apply.
- **Tests**: 10 new exact-equals tests in `Tests/DmlFormattingTests.cs` (`Format_InsertTopN_RendersInline`, `Format_InsertTopPercent_RendersInline`, `Format_InsertTopWithSelectSource_RendersInline`, `Format_InsertTopWithOutput_RendersInline`, `Format_UpdateTopN_RendersInline_PreservesScope`, `Format_UpdateTopPercent_RendersInline`, `Format_UpdateTopWithFromJoin_RendersInline_PreservesScope`, `Format_DeleteTopSimpleForm_RendersInline`, `Format_DeleteTopPercentSimpleForm_RendersInline`, `Format_DeleteTopExtendedFormWithJoin_RendersInline_PreservesScope`). All `Assert.Equal` + `Assert.NotNull(ReParse(...))`. Suite **223 → 233**.
- **Harness**: **51 / 0 / 2** unchanged. Same invariance as 4f-ii — canonical-AST equivalence is invariant under aesthetic-only changes; no harness re-bake needed.
- **What still uses `EmitGeneratorRaw`** post-4f-iii: PIVOT `ForPath` (TSqlFormatterVisitor.cs:384), UNPIVOT `ForPath` (:421), BBE-outside-clause-scope (:515 — Branch C target), `EmitConstraintNotForReplication` (:767), MERGE-source / merge-action edge cases (:2174, :2192), `EmitFragmentDefault` final fallthrough (:2364), and a handful of statement-bail paths (`SelectStatement` niche-feature niches at :104, deeply-nested CTE at :1095, etc.). The DML TopRowFilter trio is gone.
- **Out of scope / deferred**: BBE-outside-clause-scope (Branch C — next slice). 4g comments + `EmitFragmentDefault` retirement (Branch A — after C).

### 4f-iv — 2026-04-25

- **Slice motivation**: the documented col-0 leak from Known limitations § "AND/OR inside a non-clause-scope context." Multi-AND `JOIN … ON a = b AND c = d` rendered the second AND-line at column 1 because `qj.SearchCondition` was generator-rendered as one string and inlined; embedded newlines from `Sql170ScriptGenerator`'s multi-line BBE rendering survived into the output without the scope's body-column prefix. Visible in `Sorgu/Buyuk Kucuk Kasa Yeni.sql:34` (probe ground-truth) and several other corpus files (`Sorgu/1675.sql`, `Sorgu/denetim_rapor.sql`, `Sorgu/araskargoomer.sql`).
- **Probe before planning** (memory `feedback_probe_before_planning`): `Tests/TEMP_BbeQuirkProbe.cs` — disposable, deleted at slice end. Walked 11 shapes: JOIN ON single-AND / multi-AND / LEFT JOIN multi-AND / UPDATE FROM JOIN multi-AND / CASE WHEN multi-AND / CASE WHEN long multi-AND / IF predicate multi-AND / IF predicate long multi-AND / WHILE predicate multi-AND / WHERE multi-AND control / corpus shape. Captured before-state output for each.
- **Probe-driven scope reduction** (memory `feedback_pushback_when_objections_are_weak`): the original Branch C plan covered four contexts: JOIN ON, CASE WHEN, IF predicate, scalar BBE. Probe showed only JOIN ON has the col-0 leak. CASE WHEN / IF / WHILE render multi-AND single-line via `EmitInlineBooleanScaffold` squash (line 624) — generator output's lines are trimmed and space-joined. That's a *different* limitation (long predicate overflow without break), not the col-0 leak. Updated recommendation: scope reduces to JOIN ON only. Plan deviation flagged in chat before code (memory `feedback_plan_deviation_transparency`) — Ömer ack'd with "not sure I agree but go ahead," kept the smoke contingency in mind.
- **One decision, signed off before coding**:
  - **C1 — JOIN ON model**: synthetic clause scope inside `ExplicitVisit(QualifiedJoin)`. Gate: `qj.SearchCondition is BooleanBinaryExpression` triggers break-to-scope. Non-BBE keeps the inline-or-long-break heuristic from 4b-iii-a. AND/OR right-aligns with ON via existing `WriteClauseKeyword` machinery.
- **Implementation**: ~25 lines in `ExplicitVisit(QualifiedJoin)` (TSqlFormatterVisitor.cs:299-345). New branch when `SearchCondition is BooleanBinaryExpression`: `_emitter.NewLine()` + `BeginClauseScope` + `WriteClauseKeyword("ON")` + `EmitSearchConditionBody(SearchCondition)` + `_emitter.NewLine()`. Existing inline-or-long-break heuristic preserved as the `else` branch for non-BBE shapes. The synthetic ON-scope nests inside the parent QuerySpec scope; strip-parent-outer (4c step 0, SqlEmitter.cs:160-163) handles the column math correctly — verified via probe across top-level, CTE-body, and UPDATE-FROM-JOIN nesting depths.
- **No `CurrentColumn` infrastructure needed**: the original plan considered a `SqlEmitter.CurrentColumn` API to support hanging-indent BBE rendering for CASE/IF contexts. Probe showed those contexts don't have the col-0 leak, so the infrastructure isn't needed. `EmitBbeAtFixedColumn` helper sketch from the plan: not implemented.
- **`EmitGeneratorRaw(bbe)` bail at line 515 retained**: only fires if BBE is dispatched directly outside any clause scope via `EmitFragmentDefault` catchall — rare. Safety net for catchall paths; not worth removing in this slice.
- **Tests**: 6 new exact-equals in `Tests/JoinAndCteFormattingTests.cs` — `Format_InnerJoin_OnSingleAnd_BreaksToScope`, `Format_InnerJoin_OnTripleAnd_AllAndsAlign`, `Format_LeftJoin_OnMultiAnd_BreaksToScope`, `Format_InnerJoin_OnMixedAndOr_AllOperatorsAlign`, `Format_UpdateFromJoin_MultiAndOn_RendersStaircase` (corpus shape), `Format_InnerJoin_OnSingleComparison_StaysInline` (regression guard — single-cond ON not BBE, fix doesn't fire). Suite **233 → 239**. Zero existing tests modified — BBE gate preserves all locked single-cond ON shapes from 4b-iii-a (lines 25/40/54/102/220/245/268/290 of `Tests/JoinAndCteFormattingTests.cs`).
- **Harness**: **51 / 0 / 2** unchanged. Same invariance as 4f-ii / 4f-iii — canonical-AST equivalence is invariant under aesthetic-only changes.
- **Known limitations updated**: § "AND/OR inside a non-clause-scope context" simplified — JOIN ON entry retired (covered by 4f-iv); CASE WHEN / IF predicate / scalar BBE remain as separate concern (long-predicate overflow without break, distinct from the col-0 leak).
- **Out of scope / deferred**: CASE WHEN / IF / WHILE multi-AND single-line overflow — corpus-driven; if it surfaces as a real visual bug, separate slice with `EmitBbeAtFixedColumn` infrastructure. `BooleanParenthesisExpression` inside ON — falls through to default-case generator scaffold inside the synthetic scope; renders as one line including parens. Acceptable today; corpus-driven.

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
