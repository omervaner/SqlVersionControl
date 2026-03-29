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
