# Session Summary — March 30, 2026

## Who Does What

**Ömer and Claude (this chat)** are the architects. We discuss, design, debate, and produce spec documents. We don't write app code directly — we write the docs that CC follows.

**Claude Code (CC)** is the implementer. CC reads the spec docs and writes the actual Avalonia/.NET code. Ömer relays instructions between us and CC. When CC finishes something, Ömer sends screenshots for us to review.

**The workflow:** We discuss → write/update a doc → Ömer tells CC "read [doc], do [section]" → CC plans and implements → Ömer screenshots the result → we review and add new items if needed → repeat.

**When CC gets a fresh context:** Tell it which doc to read and which section to start from. CC doesn't need the full history — the docs are self-contained.

---

## What Got Done This Session

### TOOLS-MENU.md — Completed Everything
All items from the Tools Menu spec are done:
- **Redo keybinding fix** (Section 8) ✅
- **Context menu styling** (Section 10) ✅
- **SQL Quoter + Quick Quote button** (Sections 3, 7) ✅ — was almost done from previous session, Ömer fixed numeric quoting
- **Script Object As** (Section 6) ✅ — was done from previous session
- **Peek Definition** (Section 9) — Cmd+Click on proc/function/view names, loads definition in results panel ✅
- **Highlight all occurrences** (Section 11) — select a word, all matches highlighted ✅
- **Move line up/down** (Section 12) — Alt+Up/Down ✅
- **Go to line** (Section 13) — Cmd+G/Ctrl+G ✅
- **Dialog base styling** (Section 14) — unified all dialogs to match app design system ✅
- **Query Formatter** (Section 1) — using PoorMansTSqlFormatterLib NuGet, Ctrl+Shift+F ✅
- **Text Compare** (Section 2) — reuses DiffView ✅
- **Index Analysis** (Section 5) — three-tab dialog: unused indexes, missing indexes, duplicate/overlapping ✅
- **Toolbar separator** — 1px vertical line between Run/Stop and utility buttons (lightning, clock, quote, format) ✅
- **Keyboard Shortcuts dialog** — Help menu → shows all shortcuts grouped by category ✅

### EDITOR-QOL.md — Created and Completed
New doc created with five editor enhancements, all implemented:
- **Comment/Uncomment** (Section 1) — Cmd+K to comment, Cmd+L to uncomment ✅
- **Uppercase/Lowercase** (Section 2) — Cmd+Shift+U / Cmd+Shift+L ✅
- **Copy with column headers** (Section 3) — Cmd+Shift+C in results grid ✅
- **Pin result tab** (Section 4) — pin icon, pinned results survive next F5 ✅
- **Word wrap toggle** (Section 5) — Option+Z / Alt+Z ✅
- **Open Source Query** — right-click any result tab → recover the query that produced it ✅

### GIT-EXPORT.md — Created and Completed
Full DDL export to local folder (git repo):
- Exports all databases, all object types (procs, functions, views, triggers, tables)
- Organized folder structure: Server/Database/ObjectType/schema.name.sql
- Change detection: logs new, modified, deleted objects
- CHANGELOG.md appended on each export
- "Include system databases" checkbox (default: off)
- Progress dialog and summary ✅

**Immediate payoff:** CC accidentally nuked the Docker test data when re-creating the container for SQL Agent. Ömer recovered the test data from the git export. Tool paid for itself within an hour.

### ACTIVITY-MONITOR.md — Created, Partially Implemented
New view tab "Activity" with two sub-tabs:

**Tab A: Active Sessions (sp_who replacement):**
- DMV-based grid (sys.dm_exec_requests + sys.dm_exec_sessions + sys.dm_exec_sql_text)
- Kill session with confirmation dialog, can't kill own session (@@SPID check)
- Auto-refresh toggle (1s/2s/5s/10s)
- Blocking session indicators
- Status: implemented ✅

**Tab B: Jobs Dashboard:**
- Full SSMS-style Job Activity Monitor: name, enabled, status, last outcome (color coded), last run, duration, next run, category, schedule (human-readable)
- Start/Stop/Enable/Disable buttons
- **Inline Job Properties editor** (bottom detail panel with four tabs):
  - General tab — edit name, description, enabled, category
  - Steps tab — view/edit/add/delete steps
  - Schedule tab — full frequency/day/time editor
  - History tab — last 20 runs (read-only)
- Status: grid and basic detail panel done, GridSplitter bug being fixed, inline property editor tabs (General/Steps/Schedule) still need implementation

### Infrastructure Updates
- **LOCAL-DEV-NOTES.md** updated: Docker command now includes `-v sqldata:/var/opt/mssql` volume mount and `-e MSSQL_AGENT_ENABLED=true`. Never lose test data again.
- Object Dependencies (Section 4 of TOOLS-MENU) was deleted from the spec — already covered by existing dependency explorer in Version History tab.

---

## Mistakes Made — Learn From These

### 1. Suggesting features that already exist
I suggested row count, execution time, results export, and connection name in title bar — all of which already existed. Ömer had to correct me. **Rule: read CLAUDE.md and examine the actual app before suggesting features.**

### 2. Rewriting completed doc sections (AGAIN)
Despite reading the previous session's mistakes about this exact problem, I repeatedly modified completed sections in TOOLS-MENU.md and ACTIVITY-MONITOR.md instead of appending new items. Ömer had to correct me multiple times. **Rule: NEVER modify existing spec sections. ONLY append new sections. If something needs to change, add a new section that references the old one.**

### 3. Giving CC two options when one was safer
Told CC it could either enable SQL Agent on the existing container (safe) or spin up a new container (loses data). CC chose the new container and wiped the test data. **Rule: when one option is clearly safer, only give that option.**

---

## Key Design Decisions (For Future Reference)

### Turkish Keyboard Shortcuts
The Turkish Q Mac keyboard doesn't have easy access to `/` (it's Shift+7). Comment/uncomment uses Cmd+K and Cmd+L instead of the standard Cmd+/. All shortcuts were approved by Ömer before implementation.

### Query Formatter Library
Using `PoorMansTSqlFormatterLib` (MIT, NuGet) — T-SQL specific, handles procedures/batches/GO, preserves comments, fault-tolerant. Chosen over Hogimn.Sql.Formatter for better T-SQL handling.

### Activity Monitor — Inline Editing, No Popups
Job properties are edited in the detail panel below the grid (General/Steps/Schedule/History tabs), not in popup dialogs. Ömer specifically requested avoiding popups for this feature.

### Dialog Styling
All dialogs share a common base style (Section 14 of TOOLS-MENU.md). New dialogs inherit this automatically. Background matches app chrome, buttons use design system, fonts consistent.

### Git Export — Full Snapshot Model
Each export is a full snapshot — folder always reflects current server state. Deleted objects have their files removed. CHANGELOG.md appends each run. Not incremental — simple and reliable.

---

## Current State of Things

### What CC Is Doing Right Now
Fixing GridSplitter resize behavior in the Activity Monitor Jobs tab. After that, CC needs to re-read ACTIVITY-MONITOR.md to implement the expanded inline Job Properties editor (General/Steps/Schedule tabs).

### What's In Progress
- **Activity Monitor — Job Properties editor:** General, Steps, Schedule, History tabs in the detail panel. Grid and basic steps/history display done, but full editing (schedule editor, step add/delete, general tab) not yet implemented.

### What's Postponed / Future
- **DESIGN-SYSTEM.md items O and P**: dropdown border clipped, connection dialog old styling. Low priority cosmetic issues.
- **DATA-COMPARE.md**: Table data compare — already implemented from a previous session.
- **Job schedule editing dialog**: Full schedule editor with frequency/day/time pickers. Specced in ACTIVITY-MONITOR.md but not yet built.
- **Column toggling in Activity Monitor**: Hidden columns feature skipped for now per CC's recommendation.

### What's Fully Done (This Session + Previous)
- Everything in TOOLS-MENU.md (14 items)
- Everything in EDITOR-QOL.md (5 items + Open Source Query)
- Git Export (GIT-EXPORT.md)
- Activity Monitor — Active Sessions tab with Kill
- Activity Monitor — Jobs Dashboard grid with basic detail panel
- Toolbar separator between button groups
- Keyboard Shortcuts dialog

### Docs Created/Updated This Session
- `docs/TOOLS-MENU.md` — added Sections 8-14 (redo, peek definition, context menu styling, highlight occurrences, move line, go to line, dialog styling), updated priority list, updated Section 1 (formatter) and Section 5 (index analysis)
- `docs/EDITOR-QOL.md` — **NEW** — 5 editor QoL features
- `docs/GIT-EXPORT.md` — **NEW** — full DDL export spec
- `docs/ACTIVITY-MONITOR.md` — **NEW** — Activity Monitor with Sessions + Jobs Dashboard
- `docs/LOCAL-DEV-NOTES.md` — updated Docker command with volume mount and Agent flag
- `docs/SESSION-SUMMARY-2026-03-30.md` — this file

---

## Ömer's Preferences (Reinforced This Session)

- **NEVER rewrite completed doc sections. ONLY append.** This was the biggest friction point again. Add new sections, never touch old ones.
- **Read the actual app before suggesting features.** Don't suggest things that already exist.
- **When one option is safer, only give that option.** Don't give CC choices when one choice risks data loss.
- **Discuss before writing.** Hash out architectural decisions in conversation first, then write the doc. Don't write the doc and present it as done.
- **No popups when inline editing works.** Job properties editor uses the detail panel, not dialogs.
- **Turkish keyboard awareness.** Standard shortcuts like Ctrl+/ don't work on Turkish Q layout. Always check before assigning shortcuts.

---

## Plan for Next Session

Ömer plans to spin up a fresh Claude conversation to do a **security and code quality audit** of the entire codebase. Fresh eyes, no session baggage. Good areas to examine:
- Connection string handling and password storage
- SQL injection vectors (parameterized queries vs string interpolation)
- Kill session safety
- Job management safety (sp_start_job, sp_stop_job, sp_update_job)
- Error handling patterns
- Memory leaks (timers, event subscriptions, disposable objects)
- Thread safety (async/await patterns, UI thread dispatching)
