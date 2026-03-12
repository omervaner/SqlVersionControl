# SqlVersionControl v1.3.0 — Roadmap: Execution Plan Viewer

**Date:** March 12, 2026
**Current Version:** v1.2.0
**Repo:** github.com/omervaner/SqlVersionControl
**Local Path:** /Users/omer/Documents/Projects/CheatTeam

---

## Overview

A new **Execution Plan** tab that lets users generate, view, and compare SQL Server estimated execution plans — with human-readable explanations, cost breakdown visualization, code-to-plan linking, warnings, and missing index suggestions. Designed so that support staff with no DBA experience can understand what a proc is doing and where performance problems are.

### Key Dependency: PlanViewer.Core

**Do NOT build a plan XML parser from scratch.** Use the `PlanViewer.Core` library from [erikdarlingdata/PerformanceStudio](https://github.com/erikdarlingdata/PerformanceStudio) (MIT license).

This library provides:
- Full `.sqlplan` XML parser
- 30 analysis rules (implicit conversions, spills, missing indexes, memory grants, etc.)
- Operator tree with cost breakdown
- Missing index extraction with ready-to-run CREATE INDEX statements
- Warning detection and classification
- Plan comparison logic

The GUI (`PlanViewer.App`) is also Avalonia-based, so patterns can be referenced directly.

**Integration approach:** Add `PlanViewer.Core` as a project reference or copy the relevant source files into a `PlanAnalysis/` folder in the project. Do NOT take the full PlanViewer.App GUI — we build our own UI that matches SqlVersionControl's design language.

---

## Task 1: Project Setup + Plan Generation ✅ DONE

**Goal:** Get plan XML from the server for any selected proc.

**Files to modify:**
- `SqlVersionControl.csproj` — Add reference to PlanViewer.Core (either as project reference, NuGet if published, or copy source files)
- `Services/DatabaseService.cs` — Add `GetEstimatedPlanAsync(string database, string schema, string objectName)` method

**Plan generation query:**
```sql
SET SHOWPLAN_XML ON;
GO
EXEC [database].[schema].[objectName];
GO
SET SHOWPLAN_XML OFF;
```

Important: `SET SHOWPLAN_XML ON` generates the **estimated** plan WITHOUT executing the proc. Safe for procs that modify data. The XML comes back as a single-row, single-column result.

**Notes:**
- Use three-part naming so it works across databases
- Wrap in try/catch — some procs require parameters. For those, fall back to fetching the plan from `sys.dm_exec_query_stats` + `sys.dm_exec_query_plan` (cached plans)
- Store the raw XML on the model so it can be re-analyzed or exported

**Test:** Call `GetEstimatedPlanAsync` for a known proc, verify XML comes back and can be parsed by PlanViewer.Core's parser.

---

## Task 2: Execution Plan Tab — Basic Layout ✅ DONE

**Goal:** Third tab with plan visualization.

**Files to create:**
- `Views/PlanView.axaml` — Tab layout
- `Views/PlanView.axaml.cs` — Code-behind
- `ViewModels/PlanViewModel.cs` — Plan logic, analysis results

**Files to modify:**
- `Views/MainWindow.axaml` — Add "Execution Plan" RadioButton tab + PlanView control
- `Views/MainWindow.axaml.cs` — Initialize PlanView with shared services

**Tab layout (top to bottom):**

### Top Bar
- Database selector + Object selector (or inherits from Version History selection)
- Version dropdown (to pick which version's plan to view)
- "Generate Plan" button
- "Compare Plans" toggle button

### Cost Breakdown Bar
- Horizontal stacked bar spanning full width
- Each segment = one operator node, sized proportionally to its cost percentage
- Color coded by operation type:
  - Green: Index Seek, Index Scan (efficient lookups)
  - Red: Table Scan, Clustered Index Scan (full reads)
  - Yellow: Sort, Hash Match (resource-intensive)
  - Blue: Nested Loops, Merge Join
  - Grey: other operators
- Each segment is clickable — clicking highlights the corresponding code in the SQL panel and scrolls the operator tree to that node
- Segment label: human-readable name + cost percentage (e.g. "Joining orders to shipments — 47%")

### Main Content (left/right split)
- **Left panel:** SQL code (read-only, syntax highlighted, using existing DiffView-style rendering). When an operator is selected in the cost bar or tree, the corresponding SQL region is highlighted using `StatementStartOffset` / `StatementEndOffset` from the plan XML.
- **Right panel:** Operator tree (TreeView) showing the plan hierarchy. Each node shows:
  - Human-readable operation label (see Task 3 for translations)
  - Cost percentage
  - Estimated rows
  - Table/index name where applicable
  - Color-coded icon matching the cost bar

### Bottom Panel (collapsible)
- **Warnings** — extracted from plan XML, shown in red/orange
- **Missing Indexes** — with "Copy CREATE INDEX" button per suggestion

**Test:** Select a proc, click Generate Plan, verify the cost bar renders and the tree populates.

---

## Task 3: Human-Readable Operator Labels ✅ DONE

**Goal:** Replace technical SQL Server operator names with plain language descriptions.

**Implementation:** A dictionary/service that maps operator physical names to templates with placeholders for table/column/index names.

**Translations (core set):**

| SQL Server Operator | Human-Readable Label |
|---|---|
| `Hash Match (Inner Join)` | "Joining [tableA] to [tableB]" |
| `Hash Match (Aggregate)` | "Grouping results by [columns]" |
| `Nested Loops (Inner Join)` | "Looking up [tableB] for each row in [tableA]" |
| `Merge Join` | "Merging sorted [tableA] with [tableB]" |
| `Clustered Index Scan` | "Reading entire table: [table] (slow — no filter used)" |
| `Clustered Index Seek` | "Fast lookup on [table] using [index]" |
| `Index Scan` | "Scanning index: [index] on [table]" |
| `Index Seek` | "Fast index lookup: [index] on [table]" |
| `Table Scan` | "Full table read: [table] (no index available)" |
| `Sort` | "Sorting results by [columns]" |
| `Filter` | "Filtering rows where [predicate]" |
| `Compute Scalar` | "Calculating values" |
| `Stream Aggregate` | "Aggregating [function] on [columns]" |
| `Key Lookup` | "Fetching extra columns from [table] (bookmark lookup)" |
| `RID Lookup` | "Fetching row from heap: [table]" |
| `Parallelism (Gather Streams)` | "Combining parallel threads" |
| `Parallelism (Repartition Streams)` | "Redistributing data across threads" |
| `Parallelism (Distribute Streams)` | "Splitting work across threads" |
| `Top` | "Taking first [N] rows" |
| `Constant Scan` | "Generating constant values" |
| `Spool (Eager/Lazy)` | "Caching intermediate results" |

**Files to create:**
- `Services/PlanTranslator.cs` — Static class with `Translate(string operatorName, PlanNode node)` method that returns the human-readable string. Pulls table/column/index names from the node's properties.

**Notes:**
- The table, index, and column names are available in the plan XML node attributes — PlanViewer.Core's parser exposes these
- If an operator isn't in the dictionary, fall back to the raw name
- Keep the raw name accessible via tooltip for DBAs who prefer the technical terms

**Test:** Generate a plan for a proc with JOINs, sorts, and scans. Verify tree shows human-readable labels with correct table names filled in.

---

## Task 4: Code-to-Plan Linking ✅ DONE

**Goal:** Clicking a node in the cost breakdown bar or operator tree highlights the corresponding SQL code region.

**How it works:**
- Plan XML nodes contain `StatementStartOffset` and `StatementEndOffset` (character positions in the original SQL text)
- When user clicks a cost bar segment or tree node, calculate the character range and highlight it in the left SQL panel
- Reuse the same `SearchMatchRenderer` approach from the code search feature — instead of highlighting a search term, highlight a character offset range

**Files to modify:**
- `ViewModels/PlanViewModel.cs` — When a node is selected, compute highlight range and update a bound property
- `Views/PlanView.axaml.cs` — Apply highlight renderer to the SQL panel based on the selected node's offsets

**Interaction:**
- Click cost bar segment → SQL highlights + tree scrolls to node
- Click tree node → SQL highlights + cost bar segment gets a selection indicator (border or brightness change)
- Click in SQL panel → if cursor is within a statement's offset range, select the corresponding tree node and cost bar segment

**Test:** Click different segments in the cost bar, verify the SQL code highlights different regions. Click a tree node, verify same behavior.

---

## Task 5: Warnings + Missing Indexes Panel

**Goal:** Surface plan warnings and missing index suggestions prominently.

**Implementation:** PlanViewer.Core already extracts these. Just display them.

**Warnings display:**
- Collapsible panel at the bottom
- Each warning has: severity icon (red/yellow), human-readable description, affected operator
- Examples of warnings PlanViewer.Core detects:
  - Implicit conversions
  - No join predicate
  - Excessive memory grants
  - Spills to tempdb
  - Lazy spools
  - Skewed parallelism
  - Estimate vs actual row deviation

**Human-readable warning translations:**
| Warning Type | Display |
|---|---|
| Implicit conversion | "Column [col] on [table] is being converted from [typeA] to [typeB] — this prevents index usage" |
| No join predicate | "Tables [A] and [B] are joined without a condition — this creates a cross join (every row × every row)" |
| Memory grant excessive | "Query requested [X] MB of memory but only used [Y] MB — other queries may be starved" |
| Spill to tempdb | "Sort/Hash operation ran out of memory and spilled to disk — query will be slower" |

**Missing Indexes display:**
- List of suggested indexes with:
  - Target table
  - Suggested key columns
  - Suggested include columns
  - Estimated improvement percentage (if available in XML)
  - "Copy CREATE INDEX" button that copies the ready-to-run DDL to clipboard

**Files to modify:**
- `Views/PlanView.axaml` — Add bottom collapsible panel with warnings list and missing indexes list
- `ViewModels/PlanViewModel.cs` — Expose warnings and missing indexes collections from PlanViewer.Core analysis results

**Test:** Generate a plan for a proc known to have implicit conversions or missing indexes. Verify warnings display with human-readable descriptions. Click "Copy CREATE INDEX", paste, verify valid SQL.

---

## Task 6: Plan Comparison Between Versions

**Goal:** Compare execution plans across two versions of the same proc to spot performance regressions.

**Behavior:**
1. User clicks "Compare Plans" toggle in the top bar
2. Two version dropdowns appear (like the diff view in Version History)
3. Select v9 on left, v10 on right
4. App generates estimated plans for both versions
5. Display:
   - **Two cost breakdown bars** stacked (v9 on top, v10 on bottom) — visual diff of where cost shifted
   - **Side-by-side operator trees** with differences highlighted:
     - Nodes added in new version (green)
     - Nodes removed (red)
     - Nodes with changed cost (yellow, with delta shown: "47% → 12%")
   - **Summary at the top:** "v10 estimated cost: 0.45 (was 1.23 in v9) — 63% improvement" or "v10 estimated cost: 2.1 (was 0.8 in v9) — 163% regression ⚠️"
   - **Efficiency score comparison:** simple heuristic score (e.g. ratio of seeks to scans, number of warnings) for each version side by side

**Files to modify:**
- `ViewModels/PlanViewModel.cs` — Add comparison mode, dual plan generation, diff logic
- `Views/PlanView.axaml` — Comparison layout with dual cost bars and dual trees

**Notes:**
- PlanViewer.Core may have plan comparison logic already — check before building custom
- For procs not in ObjectVersions (no tracked history), comparison is unavailable — disable the button
- Each plan generation is a server call, so show a loading indicator

**Test:** Select a proc with multiple versions. Compare v1 and v2. Verify both plans render, differences are highlighted, and the summary correctly identifies improvement or regression.

---

## Task 7: Efficiency Score

**Goal:** A single number that tells non-DBAs "is this proc's plan healthy?"

**Scoring heuristic (0-100, higher is better):**
- Start at 100
- Deduct per Table Scan: -15
- Deduct per Clustered Index Scan: -10
- Deduct per Key Lookup: -5
- Deduct per implicit conversion warning: -10
- Deduct per missing index suggestion: -5
- Deduct per tempdb spill warning: -10
- Deduct per no-join-predicate warning: -20
- Bonus for all seeks (no scans): +5
- Floor at 0

**Display:** Large number with color coding in the top bar:
- 80-100: Green "Healthy"
- 50-79: Yellow "Needs attention"
- 0-49: Red "Poor — review recommended"

**In comparison mode:** Show both scores side by side with an arrow: "72 → 45 ⚠️ regression" or "45 → 85 ✓ improved"

**Files to create:**
- `Services/PlanScorer.cs` — Takes PlanViewer.Core analysis result, returns score + breakdown

**Test:** Generate plans for known good and bad procs. Verify scores make intuitive sense.

---

## Task 8: History Chart (Optional — if time permits)

**Goal:** Show plan cost over time for a proc.

**Implementation:** When generating a plan for a version, store the estimated total cost in a simple JSON file or in a new column in ObjectVersions. Show a line chart (version number on X axis, estimated cost on Y axis) so users can see cost trends.

**This task is optional for v1.3.0** — it can ship later. The core value is in Tasks 1-7.

---

## Execution Order

1. ~~**Task 1** — Plan generation + PlanViewer.Core integration (foundation)~~ ✅
2. ~~**Task 2** — Tab layout (UI shell)~~ ✅
3. ~~**Task 3** — Human-readable labels (must exist before tree is useful)~~ ✅
4. ~~**Task 4** — Code-to-plan linking (interactivity)~~ ✅
5. **Task 5** — Warnings + missing indexes (high value, easy with PlanViewer.Core)
6. **Task 7** — Efficiency score (quick, depends on warnings)
7. **Task 6** — Plan comparison (most complex, do last)
8. **Task 8** — History chart (optional stretch goal)

---

## Tech Notes

- **Always use `SET SHOWPLAN_XML ON`, never `SET STATISTICS XML ON`** — showplan generates estimated plans without executing. Safe for procs that modify data.
- **PlanViewer.Core license:** MIT. Include attribution in THIRD_PARTY_NOTICES or README.
- **Plan XML caching:** Cache generated plans in memory for the session so switching between Code/Plan views doesn't re-query the server.
- **Parameterized procs:** `SET SHOWPLAN_XML ON` + `EXEC proc` works even without parameters — it generates the plan based on cached parameter sniffing or default estimates. If no cached plan exists and params are required, surface a "provide sample parameters" input or fetch from `sys.dm_exec_cached_plans`.

---

## Release Checklist

- [x] PlanViewer.Core integrated and building
- [ ] All 7 core tasks implemented and tested (4/7 done)
- [ ] CLAUDE.md updated with Execution Plan tab architecture
- [ ] Version bumped to 1.3.0
- [ ] Attribution for PlanViewer.Core in README/THIRD_PARTY_NOTICES
- [ ] macOS + Windows builds tested
- [ ] GitHub Release with changelog
