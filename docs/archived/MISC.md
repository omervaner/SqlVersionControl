# Miscellaneous — Ideas & Issues Parking Lot

**Created:** March 30, 2026
**Purpose:** Items that need discussion or design before becoming specs. Not actionable yet.

---

## Stuck/Blocking UI States

These are places where the user can get trapped with no escape hatch.

### 1. Reconnect Overlay After Sleep/Wake — DONE (2026-03-29)
**Files:** `Views/MainWindow.axaml.cs` — `ReconnectAsync`

When the lid closes and reopens, the app shows a full-screen overlay: "Reconnecting... (attempt 1/3)". If all 3 attempts fail, you get "Connection lost" with only a Retry button. There's no Dismiss, no Work Offline, no Escape key binding. The entire app is locked out until the connection comes back.

When the lid closes and reopens, the app shows a full-screen overlay: "Reconnecting... (attempt 1/3)". If all 3 attempts fail, you get "Connection lost" with only a Retry button. There's no Dismiss, no Escape key binding. The entire app is locked out until the connection comes back.

**Need:** Escape or a "Dismiss" button hides the overlay and lets the user keep working. This isn't a "work offline mode" — it's just removing the wall. The app already handles disconnection gracefully in individual operations:

**What works without a connection (already loaded or purely local):**
- Reading/editing query text in open tabs
- Viewing results already loaded in memory
- Saved queries (open, save — local files)
- Copy/paste from result grids, Export to Excel from existing results
- Settings, theme switching
- Text Compare, SQL Quoter, Query Formatter (pure local tools)
- Viewing already-loaded version history diffs, execution plans
- Keyboard Shortcuts dialog, About

**What fails gracefully (no blocking, just error messages):**
- F5 / Run → "Not connected" in messages tab
- Object Explorer → shows stale data, refresh shows connection error
- Auto-sync → skips silently (already does this via the `_autoSyncing` guard)
- Activity Monitor → "Disconnected" in status bar, refresh shows error
- Database Compare → connection dropdowns show "Connection failed" on attempt
- Git Export → "Connection lost" in progress dialog

**What changes in code:**
- Add "Dismiss" button next to Retry on the overlay (always visible, not just after 3 failures)
- Wire Escape key to dismiss the overlay
- The reconnect attempts can still run in the background after dismiss — if they succeed, hide the overlay automatically and show a subtle "Reconnected" flash in the status bar
- A small "disconnected" indicator in the status bar (red dot or text) so the user knows the state without the overlay blocking everything
- **Connection stripe/status bar visual:** When dismissed to offline state, don't hide the stripe — desaturate it. Keep the stripe visible at ~20% opacity of the original connection color (or swap to neutral dark grey). Dot turns grey (not red — grey = inactive, red = error). Connection text appends "(offline)" in a dimmer color, e.g. "PROD TestDB (offline)". Layout stays stable, user sees which connection they *were* on, ambient color communicates the state without reading.



### 2. Compare Tab "Show Only Differences" Scan — DONE (2026-03-29)
**Files:** `ViewModels/CompareViewModel.cs` — `ScanForDifferencesAsync`

Sets `IsScanning = true` and compares every object between source and target using `SemaphoreSlim(5)`. No cancel button. With 500+ objects across two servers, this can run for minutes with no escape.

**Need:** A Cancel button that triggers a `CancellationToken`. Show partial results if cancelled mid-scan.

### 3. Git Export Progress Dialog — VERIFIED (2026-03-29, already has Cancel)
**Files:** `Views/ExportProgressDialog.axaml.cs`

Modal dialog during export. The export method accepts a `CancellationToken` but need to verify the dialog surfaces a Cancel button. If the export hangs mid-database (network drop, lock), you're stuck.

**Need:** Verify cancel button exists. If not, add one.

### 4. Deploy Operations — No Cancel, No Timeout — DONE (2026-03-29)
**Files:** `ViewModels/CompareViewModel.cs` — `DeployAsync`, `Deploy2Async`, `DeploySelectedAsync`

After confirming, the deploy runs with no progress indicator or timeout on the SQL command. If a `CREATE OR ALTER` hangs because of a lock on the target, the UI says "Deploying..." forever.

**Need:** A reasonable `CommandTimeout` (already 30s on most commands, but verify deploy commands specifically). Consider a cancel option for batch deploys.

---

## Git Export — Incremental DDL-to-Disk Sync

### The Problem

Current git export is a manual one-time full snapshot. It queries every database, every object, writes every `.sql` file. At Gratis scale (dozens of databases, thousands of procs), this takes minutes and produces a single point-in-time dump with no ongoing value. Nobody runs it regularly because it's slow. A one-time dump isn't version control.

Meanwhile, the DDL trigger + ObjectVersions table is the real source of truth for what changed. But it lives inside SQL Server — if the server goes down, if someone drops the audit database, the entire history goes with it. There's no offline survival and no external visibility.

### The Direction

**Piggyback on the 60-second auto-sync that already runs.**

`MainWindowViewModel.AutoSyncTickAsync` calls `SyncFromDdlLogAsync` every 60 seconds. It already knows exactly which objects changed — it reads `DDL_Log` entries newer than the last `SourceLogId`. After syncing to ObjectVersions, take those same changed objects and write their `.sql` files to the export folder.

That's 3-5 files per cycle, not 2000. Takes milliseconds, not minutes. The user doesn't notice, doesn't click anything, doesn't wait.

### How It Works

**Auto-sync flow (modified):**
1. Timer ticks (every 60s, existing)
2. `SyncFromDdlLogAsync` pulls new DDL_Log entries (existing)
3. Returns the list of synced entries — database, schema, object name, definition (existing data, just need to surface it)
4. **NEW:** For each synced entry, write `{ExportPath}/{Server}/{Database}/{ObjectType}/{schema.name.sql}`
5. **NEW:** If the export folder is a git repo, optionally shell out: `git add -A && git commit -m "{summary}"`

**The commit message writes itself** from DDL log data already in hand:
```
3 changes: dbo.usp_GetStock (ALTER by omer), dbo.usp_ProcessOrder (CREATE by admin), dbo.fn_CalcDiscount (ALTER by zeynep)
```

Real audit trail in git history without any extra work.

**Full snapshot stays as "first run" or "rebuild."** Run it once to seed the folder, then incremental takes over. If the folder gets corrupted or you want to reset, run the full export again from the menu. Daily operations are always incremental and invisible.

### What Changes in Code

`SyncFromDdlLogAsync` currently returns `int` (count of synced entries). Change it to return the actual entry data (or a list of changed object identifiers) so the caller can pass them to `GitExportService` for incremental file writes.

`GitExportService` needs a new method — something like:
```csharp
public async Task ExportChangedObjectsAsync(
    string connectionString, string exportPath,
    List<(string Database, string Schema, string ObjectName, string Definition)> changes)
```

This writes only the changed files, updates the CHANGELOG, and optionally runs a git commit. No full scan, no database enumeration.

The auto-sync timer in `MainWindowViewModel` calls this after a successful sync — only if `GitExportPath` is configured in settings. If not configured, nothing happens, zero overhead.

### Git Itself — Keep It Dumb

The app writes files and optionally commits. That's it. No git libraries, no branch management, no remote pushing, no merge conflict resolution. The user:
- Creates a git repo in the export folder (`git init`)
- Optionally adds a remote (`git remote add origin ...`)
- Optionally sets up a push schedule (cron, Task Scheduler, a hook, or just manual `git push`)

The app doesn't need to understand git beyond shelling out `git add -A && git commit -m "..."`. If git isn't installed or the folder isn't a repo, the file writes still happen — you just don't get commit history. Git is optional on top of the file export.

### Handling Deletes

DDL_Log captures DROP events. When the sync processes a DROP entry:
1. Delete the corresponding `.sql` file from the export folder
2. The git commit captures the deletion naturally
3. CHANGELOG records it: `- **Deleted:** dbo.usp_OldProc (DROP by admin)`

For incremental mode, this is straightforward — the DDL_Log tells you exactly what was dropped.

### Scale at Gratis

The incremental path touches only changed objects. Even with 50 databases and 10,000 procs, the daily delta is probably 10-20 objects. The bottleneck (full `sys.sql_modules` scan across all databases) only happens on initial seed or manual rebuild — never during normal operation.

### Settings Integration

In the Settings dialog (Git Integration section, already exists):
- **Export Path** — already exists, currently used for manual export
- **NEW: Auto-export on sync** — checkbox, default off. When on, every auto-sync writes changed files.
- **NEW: Auto-commit** — checkbox, default off. When on, runs `git add -A && git commit` after file writes. Only visible if auto-export is enabled.
- "Include system databases" checkbox — already exists

### What This Doesn't Do

This is a backup mechanism with audit history as a side benefit. It is NOT:
- A branching/merging workflow
- A CI/CD pipeline
- A replacement for the DDL trigger + ObjectVersions (that remains the primary version control)
- A multi-user collaboration tool

The DDL trigger is the version control. Git is the insurance policy.



---

## First-Run Setup Wizard (For Public Release)

### The Problem

The app currently assumes Gratis-style internal deployment: DDL trigger already exists, audit database already configured, someone who knows what they're doing is sitting at the keyboard. If we ship publicly, a new user opens the app and half the features silently don't work because there's no DDL trigger, no audit database, no git folder.

### The Direction

A first-run wizard that appears once, on first launch (or when no connections are configured). Two paths:

**User mode** — "I just want to write SQL"
- Skip all setup, go straight to connection dialog
- Full SQL IDE works immediately: query editor, object explorer, intellisense, editable grid, execution plans, database compare, activity monitor, job management, index analysis
- Version History tab shows a friendly "Not configured" message with a link to Settings or a "Set up now" button — not a blank/broken screen
- Git export menu item greyed out with tooltip: "Requires admin setup"
- Zero friction for people who just want a better query tool

**Admin mode** — "I want version tracking too"
- Step 1: Connect to your SQL Server (reuses connection dialog)
- Step 2: Create the DDL audit trigger — shows the script, "Run Script" button executes it on the connected server, green checkmark when done
- Step 3: Configure audit source — which database and table to read DDL_Log from (pre-fills if the script just created it)
- Step 4 (optional): Set up git export folder — file picker, optional auto-commit toggle
- Done — auto-sync starts, Version History populates on first change

### What Already Works Without Setup

This is the pitch for User mode — the app is a fully functional SQL IDE without any DDL trigger:

- Multi-tab query editor with GO batch splitting, selected-text execution, cancel
- Object Explorer with right-click context menus (Script As, Edit Data, Generate EXEC, etc.)
- Intellisense (schema-aware autocomplete)
- Editable result grid (TOAD-style inline editing with transactional apply)
- Execution plan viewer with human-readable labels and cost breakdown
- Database Compare (code objects + table structure + data compare with deploy)
- Activity Monitor (sp_who replacement + full Job Dashboard with inline editing)
- Index Analysis (unused, missing, duplicate indexes)
- SQL Quoter, Query Formatter, Text Compare
- Saved queries, session restore, dark/light themes
- Drag-and-drop from Object Explorer, Peek Definition, Quick Execute
- Copy as INSERT, Export to Excel

That's a competitive product on its own. Version tracking is the differentiator, not the minimum viable feature.

### Implementation Note

The app already handles the unconfigured state gracefully — `GetDdlSource()` returns null and sync skips. The wizard is a UX layer on top of existing behavior, not an architecture change. The key work is:
1. A `SetupWizard` dialog/view with the two-path flow
2. A "first run" flag in settings (`HasCompletedSetup: bool`)
3. Friendly empty states on Version History and Git Export when unconfigured

---

## QoL Ideas (From Codebase Audit)

### 5. Query Tab Bottom-Border Coloring
**Current state:** Colored dots (6px) on query tabs show which connection they belong to. With 5+ tabs across 3 environments, dots aren't scannable enough.

**Idea:** A thin colored bottom-border on the entire tab — like VS Code's tab decorations or Chrome's tab groups. "All the red-bottomed tabs are PROD, blue ones are DEV." The color is already stored per-tab (`TabConnectionColor`) — this is pure XAML, no logic changes.

### 6. Run on Multiple Connections
**Idea:** You have a hotfix script that needs to run on DEV, then TEST, then PROD. Right now you open 3 tabs, paste the same script, F5 each one. A "Run on..." button (or Cmd+Shift+Enter) that executes the active tab's SQL against a list of checked connections would save real time during deployments.

**Safety:** This is dangerous, so it needs the environment-aware confirmation dialog from the Connection Manager. Show each target with its environment tag, require explicit checkboxes, PROD connections get the scary red confirmation. Maybe even execute sequentially with a pause between environments so you can verify DEV worked before hitting PROD.

### 7. Searchable Query History
**Current state:** `SessionService` stores the last 10 queries in a File menu dropdown. No search, no filtering, cap too low.

**Idea:** A searchable history panel (Cmd+E or a dedicated view) with timestamp, database, connection name, row count, and the full SQL text. Search by keyword — "what was that query I ran last Tuesday against the inventory table?" Bump the cap from 10 to 100+. If it grows beyond what JSON handles well, move to a SQLite file in the app data folder. Each entry is clickable — opens in a new tab with the same database pre-selected.

### 8. Triggers in Object Explorer
**Current state:** OE loads tables, views, procs, functions, sequences, and jobs. Triggers are tracked by the DDL system (ObjectVersions stores them) but aren't browsable in the tree.

**Idea:** Add a "Triggers" folder under each database in OE. Show trigger name, parent table, enabled/disabled status. Right-click → View Definition, Script as ALTER/DROP, Enable/Disable. The data is already in `sys.triggers` / `sys.sql_modules`. For a version control tool, not being able to browse the objects you're tracking is a gap.

### 9. Find All References (Editor)
**Current state:** Peek Definition (Cmd+Click) shows one object's definition. Dependency Explorer shows what an object uses/is used by. But there's no way to ask "where is this column or table used across all procs?" from the editor.

**Idea:** Right-click a word in the editor → "Find All References" (or Cmd+Shift+F12 like VS/VS Code). Runs `SearchObjectDefinitionsAsync` (already exists) for the selected word against `sys.sql_modules`. Results appear in a panel — object name, line number, the matching line with the word highlighted. Click a result → opens the definition with cursor at that line. The infrastructure is fully built — this is just a new UI gesture wired to the existing code search.

### 10. Freeze/Pin Columns in Result Grid
**Current state:** SELECT * from a wide table, scroll right, lose the PK column. No way to freeze columns.

**Idea:** Right-click a column header → "Freeze Column" pins it to the left. Avalonia DataGrid supports `FrozenColumnCount` natively. Default: freeze 0 columns. When the user freezes one, set `FrozenColumnCount = N` where N is the column's display index + 1. A small pin/snowflake icon on frozen column headers. Right-click → "Unfreeze" to reset.

### 11. SQL Snippets / Abbreviation Expansion
**Idea:** Type `sel` + Tab → expands to `SELECT TOP 100 * FROM |` with cursor positioned after FROM. Type `ins` + Tab → `INSERT INTO | () VALUES ()`. Type `decl` + Tab → `DECLARE @| INT = NULL`.

A JSON file in the app data folder with abbreviation → expansion pairs and a `$cursor` marker for cursor position:
```json
{
  "sel": "SELECT TOP 100 * FROM $cursor",
  "ins": "INSERT INTO $cursor () VALUES ()",
  "decl": "DECLARE @$cursor INT = NULL",
  "iff": "IF EXISTS (SELECT 1 FROM $cursor)\nBEGIN\n\nEND",
  "cte": "WITH CTE AS (\n    SELECT $cursor\n)\nSELECT * FROM CTE"
}
```

User-editable — power users add their own. The expansion triggers on Tab when the cursor is immediately after a known abbreviation. No conflict with intellisense because intellisense triggers on typing, snippets trigger on Tab after a complete abbreviation.

### 12. Copy as INSERT — NULL Warning Header
**Current state:** Copy as INSERT generates `INSERT INTO ... VALUES (...)` with inline `NULL` for null values. If pasted into a table with NOT NULL constraints and no defaults, it fails on the wrong row with no obvious reason.

**Idea:** Add a comment header to the generated INSERT output:
```sql
-- 3 rows from [dbo].[Employees]
-- ⚠ 2 columns contain NULLs: [ManagerId], [EndDate]
INSERT INTO [dbo].[Employees] ...
```

Small change, saves real debugging time when deploying data between environments.

---

## Query Trace — Lightweight Profiler Replacement

### The Problem

When a WMS page is slow, or a proc is behaving unexpectedly, you need to see what SQL is actually hitting the server — with actual parameter values, durations, and read counts. Currently this means opening SSMS Profiler, configuring a trace, filtering, starting, reproducing the issue, stopping, then scrolling through a firehose of events searching for the relevant queries.

Profiler is deprecated, clunky, and streams events in real-time which is overwhelming. What you actually do is: capture for 1-2 minutes, stop, then search. Lookout can do this better.

### Three Modes, Same Infrastructure

All three modes use the same underlying mechanism: SQL Server Extended Events (XE) with a ring buffer target. No external libraries, no trace files, no Profiler dependency. Just SQL commands to create/start/stop XE sessions and read results from `sys.dm_xe_session_targets`.

#### Mode 1: Quick Trace (Editor — Ctrl+Shift+F5)

**Use case:** "I want to see every internal statement this proc executes."

You type `EXEC usp_ProcessOrders @BatchId = 500`, hit Ctrl+Shift+F5 instead of F5. The app:

1. Gets `@@SPID` from the query tab's own connection
2. Creates an XE session filtered to that SPID, capturing `sp_statement_completed`
3. Starts the session
4. Executes the query (same as F5)
5. Query finishes
6. Reads the ring buffer — every internal statement with duration, CPU, reads, rows
7. Drops the XE session (cleanup)
8. Shows results in a "Trace" results tab next to Messages

**What you see:**

| # | Statement | Duration (ms) | CPU (ms) | Reads | Rows |
|---|-----------|--------------|----------|-------|------|
| 1 | SELECT @BatchCount = COUNT(*)... | 2 | 1 | 45 | 1 |
| 2 | INSERT INTO #TempOrders SELECT... | 1,247 | 890 | 34,201 | 15,000 |
| 3 | UPDATE dbo.inventory SET qty =... | 342 | 210 | 8,100 | 15,000 |
| 4 | EXEC dbo.usp_SendNotification... | 89 | 12 | 200 | 1 |

Click a row → full statement text in a detail panel below. Instantly see that step 2 is the bottleneck.

**UX:**
- "Trace" button in toolbar next to Run (or just Ctrl+Shift+F5)
- While tracing, the Run button shows a different state (maybe "Tracing..." with a different color)
- Auto-start, auto-stop — zero configuration
- Results appear in a "Trace" tab alongside the regular Results/Messages tabs

#### Mode 2: Watch Session (Activity Monitor — Right-Click)

**Use case:** "Session 57 is blocking everything, what is it doing right now?"

From Activity Monitor, right-click a session → "Watch Session". Opens a live view:

1. Creates an XE session filtered to that SPID, capturing `sql_batch_completed` and `rpc_completed`
2. Polls the ring buffer every 1-2 seconds
3. Events appear in a grid as they complete
4. User hits Stop to end the watch
5. XE session is cleaned up

**What you see:** A real-time stream of completed statements from that session, each with duration, reads, and the full SQL with actual parameter values.

Less critical than Mode 1 and Mode 3 — this is the "nice to have" between the two.

#### Mode 3: Capture (The Profiler Replacement)

**Use case:** "Something on the WMS picking page is running a slow query. I don't know which session, which proc, or which query. I need to record everything for 2 minutes and search."

This is the mode you described — broad capture, then search after the fact.

**UI: A "Trace" tab (new top-level tab next to Activity), or a panel within Activity Monitor.**

**Before capture — Filter setup:**

| Filter | Default | Purpose |
|--------|---------|---------|
| Database | (current) | Limit to specific database |
| Login | (all) | Filter to specific login |
| Application Name | (all) | e.g. filter to WMS app name |
| Min Duration (ms) | 0 | Skip fast queries, only catch slow ones |
| Text Contains | (empty) | Pre-filter SQL text (e.g. "inventory") |

All filters optional. Leave everything blank = capture all queries on the server. For the WMS case, you'd set Database = GratisWMS and maybe Min Duration = 100ms to skip the noise.

**During capture:**

Big red "Recording" indicator. Timer showing elapsed time. Event counter showing how many captured so far. **Stop** button.

No real-time grid during capture — the ring buffer accumulates events. This is intentional: displaying thousands of events in real-time kills both UI performance and the server. Capture first, analyze after.

**After capture — Searchable results:**

| Timestamp | Database | Login | Host | Application | Duration (ms) | CPU (ms) | Reads | SQL Text |
|-----------|----------|-------|------|-------------|--------------|----------|-------|----------|
| 14:32:01.234 | GratisWMS | wms_app | WEB01 | KorberWMS | 3,421 | 2,100 | 89,000 | EXEC usp_GetPickList @Wave... |
| 14:32:01.890 | GratisWMS | wms_app | WEB02 | KorberWMS | 12 | 5 | 200 | SELECT config_value FROM... |
| 14:32:02.100 | GratisWMS | wms_app | WEB01 | KorberWMS | 1,892 | 1,400 | 45,000 | UPDATE inventory SET alloc... |

**Search box** at top — type "usp_GetPickList" or "inventory" or "WEB01" → grid filters instantly. This is the moment you find the smoking gun.

**Sort** by any column — sort by Duration descending to find the slowest queries immediately.

**Click a row** → detail panel below shows the full SQL text with actual parameter values. Not `@p1, @p2` — the real values: `@WarehouseId = 7, @WaveId = 'WAVE-2026-0329-001'`. This is critical — you need the actual values to reproduce the problem.

**Export** — Copy selected rows, Export to Excel. Same patterns as the query results grid.

### XE Implementation Details

**Events to capture:**

For Mode 1 (Quick Trace — proc internals):
```sql
sqlserver.sp_statement_completed
```
This fires for each statement inside a stored procedure. Gives you statement-level granularity.

For Mode 2 and 3 (Watch/Capture — completed queries):
```sql
sqlserver.sql_batch_completed    -- ad-hoc SQL batches
sqlserver.rpc_completed          -- stored procedure calls (includes parameter values)
```
These fire after a batch/proc completes. `rpc_completed` is the one that gives you actual parameter values.

**XE session template (Mode 3):**
```sql
CREATE EVENT SESSION [LookoutTrace_{guid}] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(
    ACTION(
        sqlserver.database_name,
        sqlserver.session_id,
        sqlserver.username,
        sqlserver.client_hostname,
        sqlserver.client_app_name,
        sqlserver.sql_text
    )
    WHERE sqlserver.database_name = N'{database}'  -- optional filter
      AND duration >= {minDurationMicroseconds}     -- optional filter
),
ADD EVENT sqlserver.rpc_completed(
    ACTION(
        sqlserver.database_name,
        sqlserver.session_id,
        sqlserver.username,
        sqlserver.client_hostname,
        sqlserver.client_app_name,
        sqlserver.sql_text
    )
    WHERE sqlserver.database_name = N'{database}'
      AND duration >= {minDurationMicroseconds}
)
ADD TARGET package0.ring_buffer(SET max_memory = 8192)  -- 8MB buffer
WITH (
    MAX_DISPATCH_LATENCY = 1 SECONDS,
    EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
    TRACK_CAUSALITY = OFF
)
```

**Reading the ring buffer:**
```sql
SELECT CAST(target_data AS XML) AS trace_data
FROM sys.dm_xe_session_targets t
JOIN sys.dm_xe_sessions s ON t.event_session_address = s.address
WHERE s.name = 'LookoutTrace_{guid}'
  AND t.target_name = 'ring_buffer'
```

Then parse the XML — each `<event>` node contains the timestamp, duration, cpu_time, logical_reads, physical_reads, sql_text, and all the ACTION fields.

**Session naming:** `LookoutTrace_{guid}` — unique per capture, easy to identify and clean up.

**Cleanup:** Always `DROP EVENT SESSION` when stopping. Also on app startup, scan for orphaned `LookoutTrace_*` sessions and drop them (handles crash recovery).

### Permissions

XE sessions require `ALTER ANY EVENT SESSION` server permission. For Ömer's admin usage at Gratis, this is fine. For non-admin users, the Trace button should check permissions first and show a clear "Insufficient permissions — requires ALTER ANY EVENT SESSION" message instead of a cryptic SQL error.

### Safety

- Ring buffer is capped at 8MB — can't fill up server memory
- `ALLOW_SINGLE_EVENT_LOSS` means the server drops events rather than blocking queries if the buffer fills
- Session is created with `ON SERVER` but only captures filtered events — minimal overhead
- Unique session name per capture — can't conflict with other traces
- Auto-cleanup on stop, crash recovery on startup
- No trace files written to the server's filesystem — everything stays in memory

### Implementation Order

1. **Mode 1: Quick Trace** — highest value, simplest build. One button, auto-lifecycle, no filters, no UI beyond a results tab. The XE session targets the tab's own SPID. Maybe 2-3 days.

2. **Mode 3: Capture** — the Profiler replacement. Filter dialog, start/stop, ring buffer reader, searchable grid with detail panel. 3-4 days.

3. **Mode 2: Watch Session** — Activity Monitor integration. Lowest priority — Mode 1 and 3 cover 95% of use cases. 1-2 days.

### What This Replaces

- SQL Server Profiler (deprecated, clunky, streams in real-time)
- Manually writing XE sessions in SSMS (error-prone, forgot to clean up, XML parsing nightmare)
- `sp_whoisactive` (third-party, snapshot only, no history)
- Staring at Activity Monitor hoping to catch the slow query in the act

### What This Doesn't Do

- Server-side trace files (`.trc`) — we use ring buffer only, no disk I/O on the server
- Replay traces — this is capture and analyze, not record and replay
- Deadlock analysis — deadlocks need their own XE event (`xml_deadlock_report`), could be added later as a separate feature
- Always-on monitoring — this is manual start/stop, not a background service


---

## Small QoL Items (Quick Builds)

### 13. Status Bar Line/Column Indicator
**Effort:** 10 minutes

Status bar shows connection info and query status but not cursor position. Every code editor shows "Ln 47, Col 12" somewhere. When an error says "line 47" you're counting lines visually right now. AvaloniaEdit fires `Caret.PositionChanged` — subscribe and update a TextBlock in the status bar.

### 14. Cmd+= / Cmd+- Font Zoom
**Effort:** 15 minutes

Currently you go to Settings to change font size. When screen sharing or squinting at a wide result set, you want instant zoom. Wire Cmd+= and Cmd+- (Ctrl on Windows) to increment/decrement `ThemeManager.ApplyTheme` with the adjusted font size. Save to settings so it persists across restarts.

### 15. Tab Right-Click Context Menu
**Effort:** 30 minutes

Tabs only have a close button. No "Close Other Tabs," "Close Tabs to the Right," "Close All," or "Duplicate Tab." The tab strip is built manually in `RebuildTabStrip` — add a right-click handler that builds a MenuFlyout. Items:
- Close
- Close Others
- Close Tabs to the Right
- Close All
- Duplicate Tab (copies SQL, database, connection)

### 16. Row Count on Result Tab Headers
**Effort:** 5 minutes

Result tabs say "Result 1," "Result 2." They should say "Result 1 (1,247 rows)." `QueryResult.RowCount` is already there — format the tab header string.

### 17. Window Title Shows Active Database
**Effort:** 5 minutes

`UpdateStatusBar` sets the title to `Lookout — {connectionDisplay}`. Should include the database: "Lookout — PROD TestDB / GratisWMS". The database name is on `ActiveTabViewModel.SelectedDatabase`. Also useful when alt-tabbing between apps.

### 18. Theme Toggle Shortcut
**Effort:** 5 minutes

Cmd+Shift+T — instant dark/light flip without opening Settings. `ThemeManager.ApplyTheme(bool isDark, int fontSize)` already exists. One keybinding in `MainWindow.OnKeyDown`, one settings save.

### 19. Duplicate Tab
**Effort:** 10 minutes

Could be in the tab right-click menu (item 15) or standalone shortcut. Creates a new tab with the same SQL text, same database, same connection. `AddNewTab` already exists — just copy SQL and database selection over after creation.

### 20. Executed Selection Flash
**Effort:** 20 minutes

When you F5 with selected text, nothing visually confirms what range got executed. A 300ms subtle background highlight on the executed selection (then fade) gives instant feedback. AvaloniaEdit `TextArea.TextView.LineTransformers` already used for occurrence highlighting — same pattern with a temporary timer to remove the highlight.


---

## Object Explorer — Missing Child Nodes (SSMS Parity)

The OE tree currently shows Tables (with Columns, Triggers), Views, Stored Procedures, Functions, Sequences, and Jobs. Compared to SSMS, it's missing several child node types that are useful daily.

### 21. Indexes Under Tables
**Daily impact:** High — "is there an index on OrderDate?" is a constant question when writing queries.

Expand a table → Indexes folder shows:
- Index name
- Type: Clustered / Nonclustered / Unique / Primary Key
- Key columns (in order)
- Included columns (if any)

**Query:** `sys.indexes` + `sys.index_columns` + `sys.columns`, same data the Index Analysis dialog already fetches but scoped to one table.

**Context menu:** View CREATE INDEX script, Drop Index.

### 22. Foreign Keys Under Tables
**Daily impact:** High — "what does this table join to?" is the first question when exploring an unfamiliar schema.

Expand a table → Keys folder (or Foreign Keys folder) shows:
- FK name
- Referenced table: `[schema].[table]`
- Columns: `local_col → referenced_col`
- On Delete / On Update action (CASCADE, SET NULL, NO ACTION)

**Query:** `sys.foreign_keys` + `sys.foreign_key_columns` + referenced object names.

**Context menu:** Script as ALTER ADD, Script as DROP.

### 23. Parameters Under Stored Procedures and Functions
**Daily impact:** High — currently you need "Generate EXEC" or "View Definition" just to see what parameters a proc takes.

Expand a proc/function → shows parameter list as children:
- `@CustomerId` INT
- `@StartDate` DATETIME
- `@Status` NVARCHAR(50) OUTPUT

Same display pattern as Columns — name, type, OUTPUT badge (like PK badge).

**Query:** `sys.parameters` + `TYPE_NAME()` — already exists as `DatabaseService.GetProcParametersAsync()`.

No context menu needed — these are read-only info nodes.

### 24. Columns Under Views
**Daily impact:** Medium — views are queried like tables, you need to see their columns.

Same expand pattern as Tables → Columns. Same query (`sys.columns` joined to `sys.views`). Same display: name, type, nullable.

Currently Views are flat leaf nodes. Adding a Columns child makes them consistent with Tables.

### 25. Constraints Under Tables (Check + Default)
**Daily impact:** Low-Medium — useful when debugging "why won't this INSERT work?" or "what's the default value?"

Expand a table → Constraints folder shows:
- Check constraints: name + check expression (`[Price] > 0`)
- Default constraints: name + column + default expression (`GETDATE()`)

**Query:** `sys.check_constraints`, `sys.default_constraints`.

**Context menu:** Script as DROP, Script as ADD.

### 26. User-Defined Types (Top-Level Folder)
**Daily impact:** Depends on usage — if Gratis uses table-valued parameters for bulk proc calls (common in WMS batch operations), you can't inspect the type structure without running a query.

New top-level folder: "Types" under each database:
- Scalar types (aliases): name + base type
- Table types: expandable with columns (same as table columns display)

**Query:** `sys.types` (user types where `is_user_defined = 1`), `sys.table_types` + `sys.columns` for table type columns.

### 27. Database-Level (DDL) Triggers
**Daily impact:** Low — but these are the triggers powering the DDL audit log. Being able to see them in the tree confirms the audit system is in place.

Could live under a "Database Triggers" folder at the database level (separate from table triggers). Shows trigger name, enabled/disabled, event types.

**Query:** `sys.triggers WHERE parent_class_desc = 'DATABASE'`.

### Suggested Implementation Order
1. **Parameters under Procs** (#23) — easiest, query already exists, highest daily payoff
2. **Indexes under Tables** (#21) — most asked question when writing queries
3. **Foreign Keys under Tables** (#22) — essential for schema exploration
4. **Columns under Views** (#24) — consistency with tables, reuses existing code
5. **Constraints** (#25), **Types** (#26), **DDL Triggers** (#27) — lower priority, add when it feels bare


### 28. Dependencies in Object Explorer (Right-Click)
**Daily impact:** High — "what calls this proc?" and "what does this proc call?" without leaving the editor.

Right-click any proc/function/view/trigger in OE → "Show Dependencies". The OE tree temporarily replaces its content with:

```
◀ Back to Object Explorer
─────────────────────────
Uses (5)
  ● dbo.inventory_detail     (Table)
  ● dbo.order_header          (Table)
  ● dbo.usp_ValidateStock     (Proc)
  ● dbo.fn_GetWarehouseId     (Function)
  ● dbo.vw_ActiveOrders        (View)
Used By (3)
  ● dbo.usp_ProcessWave        (Proc)
  ● dbo.usp_BatchAllocate      (Proc)
  ● dbo.trg_OrderInsert        (Trigger)
```

Click any dependency → peeks its definition in the results panel (same as Cmd+Click Peek Definition). Right-click a dependency → "Show Dependencies" to chain-navigate deeper. "Back" button or Escape restores the normal OE tree.

**Infrastructure already exists:** `DatabaseService.GetDependenciesAsync()` returns `(List<CodeSearchResult> Uses, List<CodeSearchResult> UsedBy)`. The Version History tab already has this exact UX in `MainWindowViewModel.ShowDependenciesAsync()` with section headers, "Back from Dependencies" button, and chain navigation. This is just wiring the same pattern to `ObjectExplorerViewModel` in the Query Editor's OE.

**Key difference from Version History's dependency mode:** The Query Editor's OE operates on live server objects (queries `sys.sql_expression_dependencies` on the connected database), while Version History's operates on tracked objects in ObjectVersions. Same data source underneath, just a different entry point.


### 29. Spotlight / Binary Name — Still Shows "SqlVersionControl"
**Effort:** 5 minutes + verify build

macOS Spotlight indexes the actual binary name. The rename to Lookout updated `Info.plist`, window titles, and dialogs, but the output binary is still `SqlVersionControl` because the csproj name wasn't changed (by design — avoids namespace churn).

**Fix:** Add `<AssemblyName>Lookout</AssemblyName>` to the csproj `<PropertyGroup>`. This changes the output binary from `SqlVersionControl` to `Lookout` without touching the csproj filename or namespace.

**Also update:**
- `.github/workflows/release.yml` — `mainExe: SqlVersionControl` → `mainExe: Lookout` (macOS), `mainExe: SqlVersionControl.exe` → `mainExe: Lookout.exe` (Windows)
- `CLAUDE.md` build commands — `--mainExe Lookout` / `--mainExe Lookout.exe`

Three files, three lines each. After that, Cmd+Space finds "Lookout."


### 30. Crash Reporter — Automatic Exception Capture
**Effort:** 1-2 hours
**Daily impact:** High — users won't reproduce or report crashes reliably. This catches them automatically.

**How it works:**

**1. Global exception handlers in `App.axaml.cs` (or `Program.cs`):**
```csharp
// Unhandled exceptions on UI thread
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    CrashLogger.LogCrash(e.ExceptionObject as Exception);

// Unhandled exceptions in async tasks
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    CrashLogger.LogCrash(e.Exception);
    e.SetObserved(); // prevent termination
};

// Avalonia-specific: RenderThread, layout exceptions
// Avalonia surfaces these through Dispatcher.UnhandledException or similar
```

**2. CrashLogger — simple static class:**

Writes to `~/Library/Application Support/Lookout/logs/crash-{timestamp}.log`:
```
=== CRASH REPORT ===
Time: 2026-03-30 21:45:12
Version: 2.3.0
OS: macOS 15.2 (arm64)
Connection: PROD WMS (10.0.0.15) / GratisWMS

Exception: System.InvalidOperationException
Message: Sequence contains no matching element
Stack Trace:
   at System.Linq.Enumerable.First[T](...)
   at SqlVersionControl.ViewModels.CompareViewModel.DeployAsync() in CompareViewModel.cs:line 287
   at SqlVersionControl.Views.MainWindow.OnDeployRequested(...) in MainWindow.cs:line 412

Active Tab: Query 3
Database: GratisWMS
Last Query: SELECT TOP 100 * FROM dbo.order_header
Editor Text (first 500 chars): ...
```

Include context that helps reproduce: which tab was active, what database, what the user was doing. The `ActiveTabViewModel` gives you all of this at crash time.

**3. On next startup — crash detection banner:**

On app launch, scan the `logs/` folder for `crash-*.log` files. If any exist:
- Show a non-blocking banner at the top: "Lookout crashed last session. [View Report] [Copy to Clipboard] [Dismiss]"
- "View Report" opens the crash log in a new query tab (it's just text)
- "Copy to Clipboard" copies the full crash report — user pastes it in Teams/Slack to Ömer
- "Dismiss" hides the banner and deletes the crash file (or moves to `logs/archive/`)

**4. Keep last 5 crash logs, delete older ones.** Prevents disk buildup from a repeating crash.

**Ties into SECURITY.md item 3.4 (file logging):** The crash reporter and the app logger use the same `logs/` folder. The app logger captures operational errors (swallowed exceptions, connection failures). The crash logger captures fatal unhandled exceptions. Same infrastructure, different severity.

**No external services needed.** Fully local. Ömer can remote into machines and read crash logs directly, or users copy-paste from the banner. If a future version wants to phone home (send crash reports to a server), the crash file is already structured — just POST it somewhere. But for internal Gratis usage, local files are enough.


### 31. Missing View Menu
**Effort:** 30 minutes

There's no View menu. Features like Toggle Object Explorer (Ctrl+B), Toggle Results Panel (Ctrl+J), Word Wrap (Alt+Z), and theme switching exist as shortcuts but aren't discoverable in any menu. A user who doesn't know shortcuts has no way to find them.

**Add between Edit and Tools:**
```
View
├── Toggle Object Explorer     Ctrl+B
├── Toggle Results Panel       Ctrl+J
├──────────────
├── Zoom In                    Cmd+=
├── Zoom Out                   Cmd+-
├── Reset Zoom
├──────────────
├── Dark Theme / Light Theme   Cmd+Shift+T  (✓ checkmark on active)
├── Word Wrap                  Alt+Z         (✓ checkmark when on)
```

Also add to **Edit menu:** Go to Line (Cmd+G) and Select All (Cmd+A) — both work as shortcuts but aren't in any menu.

### 32. Cmd+Mouse Wheel to Zoom
**Effort:** 10 minutes

Standard in every IDE (VS Code, SSMS, DataGrip). Hold Cmd (Mac) / Ctrl (Windows) and scroll the mouse wheel to increase/decrease editor font size.

Wire `PointerWheelChanged` on the editor (in `QueryTabView.axaml.cs`). Check for Cmd/Ctrl modifier. Scroll up = increment font size, scroll down = decrement. Clamp between 8 and 32. Call `ThemeManager.ApplyTheme` with the new size and save to settings. Combines with Cmd+=/Cmd+- (item 14) — both use the same underlying font size change, just different input methods.


### 33. Editor Text Selection — Unreadable in Both Themes
**Effort:** 10 minutes

The blue text selection highlight in the SQL editor is the OS-default blue (`SelectionBrush` on AvaloniaEdit's TextArea). It makes syntax-highlighted text unreadable — dark keywords disappear on bright blue background in both themes.

**Fix:** Set themed `SelectionBrush` and `SelectionForeground` on the TextArea in `QueryTabView`.

Dark theme — add to `AppTheme.axaml`:
```xml
<SolidColorBrush x:Key="EditorSelectionBrush" Color="#3a5680" Opacity="0.6"/>
<SolidColorBrush x:Key="EditorSelectionForeground" Color="#eaeaea"/>
```

Light theme — add to `AppThemeLight.axaml`:
```xml
<SolidColorBrush x:Key="EditorSelectionBrush" Color="#4a7ab5" Opacity="0.3"/>
<SolidColorBrush x:Key="EditorSelectionForeground" Color="#1a1714"/>
```

Then in `QueryTabView` where the editor is initialized (and in `RefreshTheme`):
```csharp
editor.TextArea.SelectionBrush = FindBrush("EditorSelectionBrush");
editor.TextArea.SelectionForeground = FindBrush("EditorSelectionForeground");
```

Same fix needed in `DiffView` for the read-only diff panels if they have selectable text.

### 34. Theme System — Easy to Add New Themes
**Current state:** Already easy. Two `.axaml` files (`AppTheme.axaml`, `AppThemeLight.axaml`) with identical key structures, different color values. `ThemeManager` swaps the resource dictionary. Every UI element uses `{DynamicResource}`.

**To create a new theme:**
1. Copy an existing theme `.axaml` file (e.g. `AppThemeSolarized.axaml`)
2. Change the hex color values
3. Register it in `ThemeManager` as a third option
4. Add it to the Settings dialog theme picker

**Future nice-to-have:** User-created themes via file picker — load a custom `.axaml` from the app data folder. Power users can craft their own. Low priority but architecturally trivial since the system is already resource-dictionary-based.


### 35. Editor Right-Click Context Menu
**Effort:** 45 minutes

Right-clicking inside the SQL editor does nothing. Every IDE has a context menu here. This is a discoverability gap — many features exist as shortcuts but users don't know about them.

**Menu items:**
```
Cut                          Cmd+X
Copy                         Cmd+C
Paste                        Cmd+V
──────────────
Select All                   Cmd+A
──────────────
Format SQL                   Ctrl+Shift+F
Comment Lines                Cmd+K
Uncomment Lines              Cmd+L
Uppercase                    Cmd+Shift+U
Lowercase                    Cmd+Shift+L
──────────────
Quick Quote Selection        Ctrl+Shift+Q
──────────────
Go to Line...                Cmd+G
Find                         Cmd+F
Replace                      Cmd+H
──────────────
Peek Definition              Cmd+Click   (only if cursor is on a word)
Quick Execute                Option+Click (only if cursor is on a word)
Show Dependencies                         (only if cursor is on a word)
```

The bottom three are contextual — only appear when the cursor is on a recognizable object name. The rest are always visible.

**Implementation:** In `QueryTabView.axaml.cs`, build a `MenuFlyout` on right-click (`PointerReleased` with right button on the editor). Most actions already exist as methods — `CommentLines()`, `UncommentLines()`, `FormatSqlInEditor()`, etc. Just wire them to menu items. The word-under-cursor detection for Peek/Quick Execute/Dependencies already exists from the Cmd+Click handler.


### 36. Command Palette (Cmd+E)
**Effort:** 2-3 hours
**Daily impact:** High — makes every feature in the app discoverable.

A VS Code-style fuzzy search popup. Hit Cmd+E → text box appears at the top of the editor → type any action name → filtered list of matching commands → Enter to execute, Escape to dismiss.

**What it searches:**
```
> format          → Format SQL (Ctrl+Shift+F)
> theme           → Toggle Dark/Light Theme (Cmd+Shift+T)
> kill            → Kill Session (Activity Monitor)
> quote           → Quick Quote Selection (Ctrl+Shift+Q)
> dep             → Show Dependencies
> index           → Index Analysis...
> conn            → Manage Connections... (Cmd+Shift+M)
> zoom            → Zoom In / Zoom Out / Reset Zoom
> wrap            → Toggle Word Wrap (Alt+Z)
> export          → Export to Git...
> close           → Close Tab / Close Other Tabs / Close All
> new             → New Query (Ctrl+N)
> go              → Go to Line... (Cmd+G)
> comment         → Comment Lines (Cmd+K)
> peek            → Peek Definition (Cmd+Click)
> trace           → Run with Trace (Ctrl+Shift+F5)
```

**Each entry shows:** Action name + shortcut (if one exists) + brief description. Fuzzy matching — "fmt" matches "Format SQL", "idx" matches "Index Analysis."

**Implementation:** A floating panel (popup or overlay, not a dialog) at the top center of the editor — same position as VS Code. A static registry of all commands (name, shortcut string, action callback). Filter on each keystroke. Arrow keys to navigate, Enter to execute. Dead simple data structure — just `List<(string Name, string Shortcut, string Description, Action Execute)>`.

The command list grows naturally as features are added. Every new menu item, every new shortcut automatically gets an entry in the palette. It's the single discoverability solution — users who don't read docs or memorize shortcuts can find everything here.


### 37. Better Error Feedback When Not Connected
**Effort:** 15 minutes

When you hit F5 without an active connection, the status bar just shows "× Error" with no explanation. The results panel still says "Run a query to see results here." The user has no idea the problem is the connection, not their SQL.

**Fix — two levels:**

**Results panel empty state:** Should be context-aware:
- No connection → "Not connected — select a connection in Object Explorer or File → Manage Connections"
- Connected but no database selected → "Select a database from the dropdown above"
- Connected + database selected → "Run a query to see results here" (current)

**Messages tab:** When F5 fails because of no connection, write an actual message: "Error: No active connection. Use File → Manage Connections to connect to a server." instead of just setting status bar text that disappears after 3 seconds.

**Status bar flash:** Instead of just "× Error", flash "× Not connected" — tells the user the category of problem instantly.

**Where to fix:** In `QueryTabViewModel.RunQueryAsync()`, before the `string.IsNullOrEmpty(SelectedDatabase)` early return, check the connection state first and set specific error messages.
