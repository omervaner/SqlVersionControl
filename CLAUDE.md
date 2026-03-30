# Lookout — SQL Server Desktop IDE

---
## PROJECT STATUS: v2.5.1 (March 31, 2026)

v2.5.1: OE actions use correct connection/database for new tabs, tab dot+border fade on disconnect, tab tooltips show connection info, Messages tab bar for DML/DDL.

v2.5.0: 22 UX improvements — OE double-click for Views/Functions, Ctrl+Tab, ConnectOnStartup, edit mode confirm, DML messages tab, delete connection confirm, intellisense cache invalidation, Compare tab error surfacing, and more.

v2.4.9: Extended title bar (traffic lights) macOS-only — fixes Windows title bar buttons being cut off.

v2.4.8: Fix crash on long-running queries — elapsed timer was updating UI from thread pool thread.

v2.4.7: Fix Connection Manager button states — Connect/Disconnect always visible with proper enable/disable, Connect auto-saves new connections.

v2.4.6: OE connection context menu (New Query, Disconnect), reconnect prompt on F5, session tab color preservation.

v2.4.5: Fix macOS window position drift — all dialogs use CenterOwner instead of CenterScreen.

v2.4.4: Live elapsed timer on status bar during query execution, connection dialog scrollable, password fix.

v2.4.3: Connection dialog scrollable, password preserved on mode switch, command palette uppercase/lowercase fix.

v2.4.2: Offline UX — Continue Offline on connection dialog, contextual error messages on F5, dynamic empty state.

v2.4.1: Command Palette (Cmd+E) — VS Code-style fuzzy command search.

v2.4.0: Object Explorer depth + Crash Reporter + Editor polish.

**v2.4.0 changes:**
- OE: Parameters under Procs/Functions (new `Parameter` node type, detailed type info with OUTPUT badge)
- OE: Columns under Views (expandable with same display as table columns)
- OE: Indexes under Tables (type, key columns, included columns from sys.indexes)
- OE: Foreign Keys under Tables (referenced table, column mapping, cascade actions)
- OE: Constraints under Tables (CHECK expressions, DEFAULT values)
- OE: User-Defined Types top-level folder (scalar + table types)
- OE: Database-Level DDL Triggers folder (name, enabled/disabled, event types)
- OE: Show Dependencies right-click on Proc/Function/View/Trigger — replaces tree with Uses/Used By, chain navigation, Back button
- Executed Selection Flash: 300ms blue highlight on F5'd selection range
- Crash Reporter: CrashLogger service, global exception handlers (AppDomain + TaskScheduler + main try/catch), structured crash logs with context, red banner on next startup with View Report / Copy / Dismiss
- View menu: Toggle OE (Ctrl+B), Toggle Results (Ctrl+J), Zoom In/Out/Reset, Toggle Theme, Word Wrap
- Edit menu: added Go to Line (Cmd+G), Select All (Cmd+A)
- Cmd/Ctrl+Mouse Wheel zoom on editor (8-32 range, persists to settings)
- Editor selection colors: themed SelectionBrush + SelectionForeground (readable in both themes)
- Editor right-click context menu: Cut/Copy/Paste, Format SQL, Comment/Uncomment, Upper/Lowercase, Quick Quote, Go to Line, Find/Replace, contextual Peek Definition/Quick Execute/Show Dependencies

v2.3.0: Query Trace (XE-based profiler) + QoL batch.

**v2.3.0 changes:**
- Query Trace Mode 1 — Quick Trace (Ctrl+Shift+F5): run a query with XE tracing, see every internal statement with duration/CPU/reads in a Trace result tab
- Query Trace Mode 3 — Capture (Ctrl+6): top-level Trace tab, Profiler replacement with filter setup, start/stop recording, searchable results grid with detail panel
- TraceService: XE session lifecycle (create/start/read ring buffer/stop/cleanup), permission checking, orphaned session cleanup on startup
- Toolbar "Trace" button next to Run/Stop
- Status bar Ln/Col cursor position indicator
- Cmd+=/- font zoom (persists to settings)
- Cmd+Shift+T instant dark/light theme toggle
- Window title shows active database ("Lookout — PROD WMS / GratisWMS")
- Tab right-click context menu: Close, Close Others, Close Right, Close All, Duplicate Tab
- Database dropdown preserves selection across tab switches and async loads
- OE TreeView: horizontal scroll disabled, no layout shift on selection
- Toolbar buttons tightened (22px height, MinHeight=0)

v2.2.0: Connection Manager + multi-connection Object Explorer.

**v2.2.0 changes:**
- ConnectionRegistry service: central management of all database connections with connect/disconnect, credential resolution via PasswordStore, connection state events
- SavedConnection extended with Id, Environment, TrustServerCertificate, SortOrder, ConnectOnStartup (auto-migrates legacy entries)
- Connection Manager dialog (File → Manage Connections / Cmd+Shift+M): list+edit form, color picker, environment classification, test connection, connect/disconnect
- ConnectionDialog refactored: works with registry, "Manage Connections..." button opens manager
- Compare tab rewired: dropdowns populate from registry, BuildConnectionString checks registry first, production detection uses Environment instead of IP heuristic
- Multi-connection Object Explorer: root nodes are registry connections, expand to databases, all nodes carry ConnectionId for correct connection resolution
- OE tree is global (stable across tab switches), no longer rebuilt per-tab in multi-connection mode
- Quick-switch buttons read from registry, use resolved connection strings
- Session restore: matches tabs to registry by ConnectionId first, falls back to server/database/username match, gracefully handles deleted connections
- HexToBrushConverter for DataTemplate color binding

v2.1.2: UX blocking states fixed — reconnect overlay dismissable, scan/deploy cancellable.

**v2.1.2 changes:**
- Reconnect overlay: Dismiss button + Escape key, background retry every 10s, offline status bar (grey dot, desaturated stripe, "(offline)" suffix)
- Compare scan: Cancel button with CancellationToken, shows partial results on cancel
- Batch deploy: Cancel button, per-object progress ("Deploying 3/17: usp_GetStock..."), explicit 30s CommandTimeout on all deploy commands
- Git Export cancel: verified already working (CTS wired to Cancel button)
- Window title updated to "Lookout" in all states

**v2.1.1 changes:**
- Security: Connection string building now uses SqlConnectionStringBuilder (fixes injection via semicolons in passwords)
- Security: TrustServerCertificate now configurable per-connection (default true, prep for public release)
- Security: Single-quote escaping for DB_ID() in index analysis queries
- Security: DDL audit table source bracket-escaped + Settings input validation
- Quality: Application-level file logger (AppLogger) replaces all Console.WriteLine — writes to logs/app.log with 5MB rotation
- Quality: RollbackToVersionAsync now returns actual error messages instead of generic "check permissions"
- Quality: ConvertToCreateOrAlter deduplicated (single source of truth in DatabaseService)
- Quality: ActivityViewModel implements IDisposable, disposed on app close
- Quality: GetTableStructureAsync made static, no more orphan DatabaseService instances
- Quality: CancellationTokenSource properly disposed before reassignment
- Quality: Schedule removal now has confirmation dialog
- Quality: AlterSequenceDialog shows warning about duplicate keys / skipped ranges
- Quality: SPID re-fetched each refresh cycle (no more stale self-kill check)
- Quality: PasswordStore uses ConcurrentDictionary for thread safety
- Quality: FormatValue handles numeric types explicitly, unsupported types get /* comment */ fallback
- Quality: Auto-sync timer stopped on app close

**v2.1.0 changes:**
- App renamed to "Lookout" — all user-facing surfaces updated, config folder auto-migrated
- Quick Execute (Shift+Click) — click a proc name to open a new tab with ready-to-run EXEC template with typed parameters

**v2.0.0 changes:**
- Git Export (File → Export to Git / Settings → Export Now) — full snapshot export of all database objects as .sql files, change detection, cleanup of deleted objects, CHANGELOG.md generation, progress dialog with summary
- Activity Monitor tab (Cmd+5) — real-time server monitoring with two sub-tabs:
  - Sessions: sp_who replacement with DMV queries, blocking chain visualization, Kill Session with safety checks, auto-refresh, filters
  - Jobs Dashboard: full SQL Agent job monitoring with color-coded status, human-readable schedules, inline job property editor (General/Steps/Schedule/History tabs), Start/Stop/Enable/Disable actions
- AccentGreen theme resource added to both themes

**v1.9.0 changes:**
- Index Analysis dialog (Tools → Index Analysis) — three-tab DMV analysis: unused indexes, missing indexes, duplicate/overlapping indexes with DROP/CREATE script generation and CSV export
- Comment/Uncomment lines (Cmd+K / Cmd+L)
- Uppercase/Lowercase selection (Cmd+Shift+U / Cmd+Shift+L)
- Copy results with column headers (Cmd+Shift+C) + context menu
- Pin Result Tab — preserve result tabs across query re-runs, with unpin via context menu
- Word Wrap toggle (Option+Z / Alt+Z) + Edit menu item
- Keyboard Shortcuts dialog (Help → Keyboard Shortcuts) — grouped by category, platform-aware symbols
- Right-click result tab → Open Source Query (opens original SQL in new tab)
- Editor placeholder text (disappears on focus)
- Fix: Copy as INSERT crash on boolean columns (InvalidCastException)

**v1.8.4 changes:**
- Query Formatter (Ctrl+Shift+F) — T-SQL formatting via Hogimn.Sql.Formatter, toolbar F button
- Text Compare dialog (Tools → Text Compare) — reuses DiffView component
- Dialog base styling — unified background, fonts, inputs, button padding across all 11 dialogs
- Toolbar separator between Run/Stop and utility buttons (4px/8px spacing)

**v1.8.3 changes:**
- Tools menu with SQL Quoter dialog (paste values, get IN clause output)
- Quick Quote toolbar button (`"` icon, Ctrl+Shift+Q) — quotes selected text in-place
- Script Object As context menus (CREATE, ALTER, DROP, INSERT for tables; ALTER/DROP for procs/views/functions)
- Peek Definition (Cmd+Click / Ctrl+Click on proc/view/function names)
- Highlight all occurrences of selected word (case-insensitive, whole-word)
- Move line up/down (Alt+Up/Down)
- Go to Line (Cmd+G / Ctrl+G)
- Redo keybinding fix (Cmd+Shift+Z / Ctrl+Y)
- Context menu styling (12px font, themed background)

v1.8.2: Major design system overhaul + architecture changes. See docs/ folder for full specs.

**v1.8.x changes (March 29 session):**
- Complete visual redesign — DESIGN-SYSTEM.md governs all UI decisions
- Merged toolbar into query tab row (4 bars → 3 bars before editor)
- Merged main view tabs into menu bar row (3 bars → 2 bars before editor)
- Warm cream light theme with full theme switching (ThemeChanged event system)
- Shortened tab labels: Editor, History, Compare, Exec Plan, Settings
- Removed Query menu (redundant — Run/Stop already in toolbar)
- Per-view connection ownership — each view remembers its own connection, status bar mirrors active view
- Colored dots on query tabs showing their connection environment
- Connection stripe gradient fade (transparent at edges)
- OE density restored, 20px left padding, compact filter box
- Results panel collapsed by default, expands on F5 (70/30 split)
- Overlay auto-hide scrollbars
- Configurable grid row height and monospace font size in Settings
- DDL audit source configurable in Settings (no more hardcoded VMAuditDb)
- Git integration path in Settings (UI only, export logic pending)
- Closed-eye logo (monochrome, line-art, works at all sizes)
- Tooltips on all toolbar buttons with keyboard shortcuts
- CI fix: publish with `-f net9.0` flag (csproj targets both net9.0 and net10.0)

v1.7.0: Collapsible panels, Sequences in OE, Table Structure Compare, SQL Agent Jobs. v1.6.0: Per-tab connections + quick-switch buttons. v1.5.0: Performance, session, connections & data tools. v1.4.0: Menu bar, multi-tab query editor, editable grid, saved queries. v1.3.0: Execution Plan tab. v1.2.0: Unified search, dependency explorer, sleep/wake recovery, encrypted passwords, auto-sync.
---

## Project Identity
- **Project Name**: Lookout
- **Folder**: `/Users/omer/Documents/Projects/SqlVersionControl`
- **Repository**: omervaner/SqlVersionControl
- **Purpose**: Cross-platform SQL Server desktop IDE

## What This App Does

### Tab 1: Query Editor (v1.4.0)
Write and run SQL queries against any database on the connected server:
- **Multi-tab editor**: SSMS-style tabbed interface (Ctrl+N new tab, Ctrl+W close tab, middle-click close)
- **Object Explorer** stays shared (left panel), each tab has its own editor, database dropdown, Run/Stop, results grid
- **Tab strip**: manual StackPanel with close (×) buttons and "+" add button; minimum 1 tab always open
- **SQL editor**: AvaloniaEdit with TSQL syntax highlighting, line numbers, Consolas font
- **Database picker**: dropdown to select target database (uses dedicated connection, doesn't block DDL sync)
- **Run (F5 / Ctrl+Enter)**: executes full text or selected text only (SSMS-style)
- **Stop**: cancels running query via CancellationToken + SqlCommand.Cancel()
- **GO batch splitting**: splits on `GO` lines, executes batches sequentially
- **Results grid**: DataGrid with auto-generated columns, read-only, one tab per result set
- **Messages tab**: execution time, row counts, PRINT output, errors with line numbers
- **InfoMessage wired before OpenAsync** so early PRINT messages are captured
- **Object Explorer context menus** (right-click): Table → SELECT TOP 100, SELECT COUNT(*), Script as CREATE; View → SELECT TOP 100, View Definition; Proc → View Definition, Generate EXEC with param placeholders; Function → View Definition; Column → SELECT DISTINCT, Insert Column Name; Job → View Job Steps, View History, Start Job (with confirmation), Refresh
- **SQL Agent Jobs** in Object Explorer: Jobs folder under each server, lazy-loaded from `msdb.dbo.sysjobs` + `sysjobhistory` + `sysjobactivity`. Shows job name, enabled/disabled status, last run outcome (Success/Failed/Running). Context menu for viewing steps, history, and starting jobs.
- **Double-click quick actions**: Table → SELECT TOP 100 (auto-run); Proc → View Definition; Column → Insert column name at cursor
- **Drag-and-drop** from Object Explorer into editor: Table/View → `[schema].[name]` at drop position; Function → `[schema].[name]()`; Column → `[name]`; Proc → opens full definition
- **Editable result grid** (TOAD-style): For simple single-table SELECTs with a PK, "Edit" button appears on result tab header. Toggle enters edit mode — DataGrid becomes writable. Row-based change tracking: snapshot on row enter, compare on row leave, Escape reverts. Yellow=modified, green=new, red=deleted. "Mark for Delete" via right-click. "Add Row" for inserts. "Show SQL" previews parameterized DML. "Apply" executes in a single transaction with row-count verification. "Edit Data" context menu on tables auto-runs SELECT TOP 200 and enters edit mode.

### Menu Bar + View Tabs (v1.8.2)
Merged into a single row: menus left-aligned, view tabs right-aligned. Only 2 bars before SQL editor content.

Layout: `[traffic lights] File  Edit  Help  ——  Editor  History  Compare  Exec Plan  Settings`

- **File**: New Query (Ctrl+N), Open File (Ctrl+O), Save (Ctrl+S), Save As (Ctrl+Shift+S), Change Connection, Recent Files submenu, Exit
- **Edit**: Undo, Redo, Cut, Copy, Paste, Find (Ctrl+F), Replace (Ctrl+H) — pass-through to active tab's AvaloniaEdit
- **Help**: About (shows version dialog), Check for Updates (opens GitHub releases)
- **Query menu removed** — Run/Stop are toolbar buttons, Change Connection moved to File
- **View tabs**: Editor, History, Compare, Exec Plan are RadioButtons styled as tabs
- **Settings**: text button, opens SettingsDialog (replaces old gear icon)

### Saved Queries (v1.4.0 Task 7)
Full .sql file persistence for query tabs:
- **Save (Ctrl+S)**: If query has been saved before, overwrites silently. If new, shows Save dialog for naming.
- **Save As (Ctrl+Shift+S)**: Always shows Save dialog for a new name/file.
- **Open (Ctrl+O)**: Shows Open Query dialog listing all saved queries with search/filter, or Browse for any .sql file on disk.
- **Recent Files**: File menu submenu with last 10 opened/saved queries.
- **Tab titles**: Saved queries show their name instead of "Query N". Unsaved changes append " *" asterisk.
- **Close tab integration**: "Save" button in the unsaved changes dialog now actually saves before closing.
- **Storage**: `~/Library/Application Support/Lookout/queries/` (macOS) / `%APPDATA%/Lookout/queries/` (Windows)
- **File format**: Plain `.sql` with metadata comment header (Name, Database, Created, Modified timestamps)
- **Services**: `QueryFileService` handles all file I/O; `SettingsService` tracks recent query paths.

### Tab 2: Version History
Track changes to stored procedures, functions, views, and triggers over time. Syncs from a DDL audit log (`VMAuditDb.dbo.DDL_Log`) and stores versions in `ObjectVersions` table. Features:
- Object browser with version counts
- Recent changes grid showing who changed what and when
- Side-by-side diff view comparing any two versions (SelectableTextBlock for copy support)
- Rollback capability to restore previous versions
- **Unified search**: search box filters by object name instantly, then searches inside definitions (code search) after a 400ms debounce. Code-only matches appear with "(in code)" marker
- **Dependency explorer**: select an object → click Dependencies → Object Browser shows "Uses" / "Used By" sections with chain navigation
- **Auto-sync timer**: 60-second background polling syncs new DDL changes without resetting user selection
- **Copy buttons**: Copy Left / Copy Right buttons on the version selector bar

### Tab 3: Database Compare
Compare objects between two (or three) databases and deploy changes:
- Source → Target1 comparison with deploy
- Optional Target2 for three-way comparison (Source → Target1 → Target2)
- Batch selection and deploy for multiple objects
- "Show only differences" mode with progress scanning

### Tab 4: Execution Plan (v1.3.0)
Generate and visualize SQL Server estimated execution plans with human-readable explanations:
- **Plan generation**: `SET SHOWPLAN_XML ON` + `EXEC proc` for safe estimated plans
- **Cost breakdown bar**: horizontal stacked bar showing operator costs proportionally, color-coded by type, clickable
- **Operator tree**: flattened tree view with human-readable labels (via `PlanTranslator`), cost %, estimated rows, table/index info
- **Human-readable labels**: "Reading entire table: Orders (slow — no filter used)" instead of "Clustered Index Scan"; raw names in tooltips for DBAs
- **Code-to-plan linking**: clicking a tree node or cost bar segment highlights the corresponding SQL statement; offsets parsed from raw XML via `PlanXmlHelper`
- **Warnings panel**: plan warnings (implicit conversions, spills, etc.) shown at bottom
- **Missing indexes**: suggestions with "Copy CREATE INDEX" button
- **Uses PlanViewer.Core** (MIT, from erikdarlingdata/PerformanceStudio) for XML parsing and analysis — lib is a git submodule, do NOT modify files inside `lib/PerformanceStudio/`

## Tech Stack
- **Framework**: Avalonia UI 11.x (.NET 9)
- **Models**: ConnectionSettings, ObjectVersion, QueryResult, EditableRow (IEditableObject + INotifyPropertyChanged, row-based change tracking), TableColumnInfo, TableCompareResult, QueryFileInfo
- **Pattern**: MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **SQL**: Microsoft.Data.SqlClient
- **Diff Engine**: DiffPlex for side-by-side comparison
- **Platforms**: macOS (ARM64), Windows (x64)

## Project Structure
```
SqlVersionControl/
├── Views/           - Avalonia XAML views and code-behind
├── ViewModels/      - MVVM view models
├── Models/          - Data models (ConnectionSettings, ObjectVersion, QueryResult, etc.)
├── Services/        - DatabaseService, SettingsService, ThemeManager, etc.
├── Styles/          - AppTheme.axaml (dark), AppThemeLight.axaml (warm cream light)
├── Assets/          - Logo SVGs (logo.svg, logo-dark.svg, logo-light.svg), backup icons
├── docs/            - Design & planning docs (read these first for context)
│   ├── DESIGN-SYSTEM.md      - Visual design bible (colors, spacing, components, phases)
│   ├── QUALITY-POLISH.md     - Architecture changes (menu merge, connection model, stripe)
│   ├── DATA-COMPARE.md       - Table data compare feature spec
│   ├── TOOLS-MENU.md         - Tools menu features (quoter, formatter, dependencies, etc.)
│   ├── LOCAL-DEV-NOTES.md    - Docker dev environment setup
│   └── SESSION-SUMMARY-*.md  - Session logs
├── lib/             - Git submodule: PlanViewer.Core (DO NOT MODIFY)
├── CLAUDE.md        - This file (developer guide)
└── SqlVersionControl.csproj
```

## Key Files

### Views
| File | Purpose |
|------|---------|
| `MainWindow.axaml(.cs)` | Main app window with tabs, object browser, diff view |
| `CompareView.axaml(.cs)` | Database comparison tab (Source ↔ Target) |
| `DiffView.axaml(.cs)` | Reusable side-by-side diff control with syntax highlighting |
| `ConnectionDialog.axaml(.cs)` | Initial database connection dialog |
| `QuickConnectionDialog.axaml(.cs)` | Quick add connection in Compare tab |
| `SettingsDialog.axaml(.cs)` | App settings with live theme preview |
| `DeployDialog.axaml(.cs)` | Confirmation dialog for deployments |
| `RollbackDialog.axaml(.cs)` | Confirmation dialog for rollbacks |
| `QueryEditorHost.axaml(.cs)` | Query Editor host — Object Explorer + multi-tab management |
| `QueryTabView.axaml(.cs)` | Per-tab query UI — toolbar, editor, results grid, messages |
| `AboutDialog.axaml(.cs)` | About dialog (version, GitHub link) |
| `PlanView.axaml(.cs)` | Execution Plan tab — cost bar, operator tree, SQL panel with code-to-plan linking |
| `SaveQueryDialog.axaml(.cs)` | Save query dialog — name input, database label |
| `OpenQueryDialog.axaml(.cs)` | Open query dialog — saved queries list, search, browse |
| `CloseTabDialog.axaml(.cs)` | Unsaved changes prompt — Save/Don't Save/Cancel |

### ViewModels
| File | Purpose |
|------|---------|
| `MainWindowViewModel.cs` | Manages version history, object browser, recent changes |
| `CompareViewModel.cs` | Manages database comparison, deploy functionality, three-way compare |
| `ConnectionViewModel.cs` | Handles connection form logic |
| `QueryTabViewModel.cs` | Per-tab query VM — run/stop, results, database selection |
| `QueryEditorHostViewModel.cs` | Host VM — holds shared ObjectExplorerViewModel |
| `PlanViewModel.cs` | Execution plan generation, operator tree, cost segments, highlight range |

### Services
| File | Purpose |
|------|---------|
| `DatabaseService.cs` | All SQL Server operations (queries, deploy, rollback, code search, dependencies) |
| `SettingsService.cs` | JSON-based settings persistence, recent connections |
| `ThemeManager.cs` | Dark/light theme colors, font size management |
| `SqlSyntaxHighlighter.cs` | SQL keyword/string/comment highlighting |
| `PasswordStore.cs` | Encrypted password persistence (DPAPI on Windows, AES on macOS) |
| `SleepDetector.cs` | Timer-based sleep/wake detection with `WokeFromSleep` event |
| `PlanTranslator.cs` | Translates raw SQL Server operator names to human-readable labels |
| `PlanXmlHelper.cs` | Extracts statement character offsets from raw plan XML for code-to-plan linking |
| `DataEditService.cs` | Editable grid: simple SELECT parsing, PK fetching, DML generation, transactional apply |
| `QueryFileService.cs` | Saved queries: save/load/list/delete .sql files with metadata headers |
| `UpdateService.cs` | Velopack auto-update: check GitHub Releases, download, apply & restart |
| `TableCompareService.cs` | Table structure comparison: column-level diffs, CREATE TABLE / ALTER TABLE DDL generation |
| `SqlTypeFormatter.cs` | SQL type formatting helper (NVARCHAR(50), DECIMAL(18,2), etc.) |
| `IntellisenseService.cs` | Schema-aware autocomplete: context detection + suggestion generation |
| `SqlCompletionData.cs` | AvaloniaEdit ICompletionData implementation for SQL suggestions |
| `SessionService.cs` | Session save/restore for query tabs, per-tab connections |
| `AppVersion.cs` | Static version string helper (reads from assembly) |

---

## ⚠️ THE #1 RULE — SINGLE SOURCE OF TRUTH ⚠️

**If you are about to write the same logic in a second place, STOP. Make it a method and call it from both places.**

This is the most important rule in the entire project. Duplicated logic with slight variations is the #1 source of bugs. Every time code gets copy-pasted and modified, the copies drift apart and cause bugs that take hours to find.

**Real examples from this project that caused painful debugging sessions:**
- DataGrid column building was copy-pasted into THREE places in QueryTabView.axaml.cs (SelectResultTab, OnEditModeChanged enter, OnEditModeChanged exit). When NULL display was added, only one copy got the converter. Result: NULLs worked in read-only mode but disappeared in edit mode.
- Syntax highlighting loading used two different approaches (disk file in QueryTabView, embedded resource in DiffView). The disk approach silently failed, falling back to built-in TSQL with wrong colors. Result: keywords were unreadable dark blue for weeks.
- Database list population was duplicated across MainWindowViewModel, QueryEditorViewModel, and CompareViewModel. The save/restore pattern for SelectedDatabase was applied inconsistently. Result: dropdowns kept going blank.

**The pattern:**
```csharp
// BAD: Same logic in multiple places
void MethodA() { /* build columns with converter */ }
void MethodB() { /* build columns without converter — oops */ }
void MethodC() { /* build columns with wrong binding mode — oops */ }

// GOOD: One method, called everywhere
void BuildColumns(QueryResult result, bool isEditMode) { /* single source of truth */ }
void MethodA() { BuildColumns(result, false); }
void MethodB() { BuildColumns(result, true); }
void MethodC() { BuildColumns(result, false); }
```

**Before writing ANY code, ask: does this logic already exist somewhere? If yes, extract it into a shared method.**

---

## Critical Architecture Patterns

### 1. Settings Sharing (IMPORTANT!)
`SettingsService` must be a SINGLE shared instance across all views:
```csharp
// MainWindow.cs creates it
_settings = new SettingsService();

// Pass to dialogs
new ConnectionDialog(_viewModel.DatabaseService, _settings)

// Pass to CompareView
compareView.Initialize(_settings)
```
**Why**: Previously, CompareViewModel created its own SettingsService, which broke connection persistence on Windows.

### 2. Auto-Connect Source After Target Connects
After user connects to target (enters password), automatically connect source using stored credentials from main app login.
```csharp
// In ConnectTargetAsync - after target successfully connects
if (IsTargetConnected && SelectedSourceConnection != null && !IsSourceConnected)
{
    // Try to connect source silently (password should be in PasswordStore from main login)
    await ConnectSourceAsync(SelectedSourceConnection);
}
```
This gives seamless UX: user only enters ONE password (for target), source connects automatically using already-stored credentials.

### 3. Deploy Script Conversion
**ALWAYS** convert `CREATE` to `CREATE OR ALTER` for idempotent deploys:
```csharp
private static string ConvertToCreateOrAlter(string definition)
{
    // Skip if already has "OR ALTER"
    if (Regex.IsMatch(definition, @"CREATE\s+OR\s+ALTER", RegexOptions.IgnoreCase))
        return definition;

    // Flexible pattern that works with comments/whitespace before CREATE
    var pattern = @"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b";
    return Regex.Replace(definition, pattern, "CREATE OR ALTER $1",
        RegexOptions.IgnoreCase);
}
```
Used in: `CompareViewModel.DeployAsync()`, `DeploySelectedAsync()`, `DatabaseService.RollbackToVersionAsync()`

### 4. Theme-Aware Dynamic Resources
Use Avalonia dynamic resources instead of hardcoded colors:
```xml
<!-- BAD: Hardcoded (only works in light theme) -->
<Border Background="#f5f5f5"/>

<!-- GOOD: Adapts to theme -->
<Border Background="{DynamicResource SystemControlBackgroundChromeMediumLowBrush}"/>
```
Common resources:
- `SystemControlBackgroundChromeMediumLowBrush` - panel backgrounds
- `SystemControlBackgroundChromeMediumBrush` - header backgrounds
- `SystemControlForegroundBaseMediumLowBrush` - borders, splitters
- `SystemControlForegroundBaseMediumBrush` - secondary text

---

## Build & Release

### Debug Build
```bash
dotnet build -f net10.0    # or net9.0 on machines without .NET 10
dotnet run -f net10.0
```

### ⚠️ IMPORTANT: Multi-Target & Velopack Release Process

The csproj targets **both net9.0 and net10.0** (`<TargetFrameworks>net9.0;net10.0</TargetFrameworks>`).
- **Always publish with `-f net9.0`** for releases — this ensures the self-contained binary works on both net9.0 and net10.0 machines.
- **Always run debug builds with `-f net10.0`** on the home Mac (which has .NET 10 installed).
- The `vpk` CLI tool (Velopack v0.0.1298) targets .NET 9. On a .NET 10-only machine, run it with `DOTNET_ROLL_FORWARD=LatestMajor` to force it to use .NET 10 runtime.
- **Do NOT use `vpk upload github`** — it requires a `--token` env var. Use `gh release create` instead to upload assets.

### Full Release Checklist
```bash
# 1. Bump version in SqlVersionControl.csproj (<Version>X.Y.Z</Version>)
# 2. Update CLAUDE.md project status header

# 3. Commit and push
git add <files>
git commit -m "vX.Y.Z — description"
git push origin main

# 4. Publish (ALWAYS use -f net9.0 for releases)
dotnet publish -c Release -r osx-arm64 --self-contained -f net9.0 -o publish/osx-arm64

# 5. Velopack package (use DOTNET_ROLL_FORWARD on .NET 10 machines)
DOTNET_ROLL_FORWARD=LatestMajor vpk pack \
  --packId Lookout --packVersion X.Y.Z \
  --packDir publish/osx-arm64 --mainExe SqlVersionControl \
  --icon AppIcon.icns --outputDir Releases

# 6. Create GitHub release with assets (use gh CLI, NOT vpk upload)
gh release create vX.Y.Z \
  Releases/Lookout-X.Y.Z-osx-full.nupkg \
  Releases/Lookout-osx-Portable.zip \
  Releases/Lookout-osx-Setup.pkg \
  Releases/RELEASES-osx \
  Releases/releases.osx.json \
  Releases/assets.osx.json \
  --repo omervaner/SqlVersionControl \
  --title "vX.Y.Z" \
  --notes-file /tmp/release-notes.md
```

### Windows Build (when needed)
```bash
dotnet publish -c Release -r win-x64 --self-contained -f net9.0 -o publish/win-x64
DOTNET_ROLL_FORWARD=LatestMajor vpk pack \
  --packId Lookout --packVersion X.Y.Z \
  --packDir publish/win-x64 --mainExe SqlVersionControl.exe \
  --icon Assets/AppIcon.ico --outputDir Releases
```

---

## Trials and Tribulations (Bug History)

### Issue 1: Deploy fails if object exists
**Symptom**: Deploying a stored procedure that already exists in target threw "object already exists" error.
**Root Cause**: Using raw `CREATE PROCEDURE` instead of `CREATE OR ALTER PROCEDURE`.
**Fix**: Added `ConvertToCreateOrAlter()` regex transformation before executing deploy scripts.
**Lesson**: SQL Server 2016+ supports `CREATE OR ALTER` for idempotent deployments.

### Issue 2: CREATE OR ALTER regex too strict
**Symptom**: Deploy still failed for some stored procedures.
**Root Cause**: Original regex `^\s*CREATE\s+` required CREATE at line start. Failed when SQL had comments before CREATE.
**Fix**: Changed to `\bCREATE\s+` (word boundary) and added pre-check for existing "OR ALTER".

### Issue 3: Light theme not applying to Compare tab
**Symptom**: Switching to light theme only affected Version History tab, Compare tab stayed dark.
**Root Cause**: DiffView controls in Compare tab weren't having `ApplyTheme()` called.
**Fix**: Added `compareView.RefreshTheme()` call after initialization in MainWindow.

### Issue 4: Auto-restored connection doesn't load objects
**Symptom**: When Compare tab opens, source dropdown shows last connection but object list is empty. Had to manually re-select source.
**Root Cause**: `RestoreLastComparison()` was called in ViewModel constructor, BEFORE `PasswordRequested` event handler was wired up. For SQL Auth, password prompt silently failed.
**Fix**: Created async `RestoreAndConnectAsync()` method, called it from `Initialize()` AFTER event handlers are wired.

### Issue 5: Search with underscores fails
**Symptom**: Typing "just_ship" found nothing, but "just ship" (spaces) found "just_ship_stuff".
**Root Cause**: Search normalized object names (replaced `_` with space) but didn't normalize search text.
**Fix**: Added `SearchText.Replace("_", " ")` before splitting into search terms.

### Issue 6: Target2 controls overflow off-screen
**Symptom**: Adding third database pushed UI elements off the right edge.
**Root Cause**: All connection dropdowns were in one horizontal row.
**Fix**: Redesigned to two-row layout. Row 1: Source + Search + Target1 + toggle. Row 2 (conditional): Target2.

### Issue 7: Dark theme colors unreadable
**Symptom**: Dark theme had poor contrast, hardcoded light colors like `#f5f5f5`.
**Root Cause**: AXAML used hardcoded colors that don't adapt to theme.
**Fix**: Replaced with `{DynamicResource ...}` bindings to Avalonia's theme-aware brushes.

### Issue 8: Windows connections not persisting
**Symptom**: Connections saved on one session weren't available after restart on Windows.
**Root Cause**: CompareViewModel was creating its own `SettingsService` instance instead of using shared one.
**Fix**: Pass single `SettingsService` instance from MainWindow through all views.

### Issue 9: macOS icon disappears after publish
**Symptom**: App bundle had no icon after `dotnet publish`.
**Root Cause**: Publish doesn't include app bundle structure, must create manually.
**Fix**: Manual creation of `.app/Contents/{MacOS,Resources}` structure, copy `AppIcon.icns`.

### Issue 10: "App is damaged" on macOS
**Symptom**: macOS Gatekeeper blocked the app.
**Root Cause**: Unsigned app from internet.
**Fix**: Code sign with `codesign --force --deep --sign -` (ad-hoc signing).

### Issue 11-13: Compare tab auto-connect saga (RESOLVED)
**Symptom**: Multiple attempts to make Compare tab auto-connect source failed:
- Issue 11: Double password prompt when switching tabs
- Issue 12: Refresh button didn't connect restored selections
- Issue 13: TryAutoConnectSourceAsync failed silently (visual tree not attached)

**Root Cause**: Trying to auto-connect at the WRONG time (tab attach, visual tree events, etc.)

**Final Fix**: Auto-connect source AFTER target connects successfully in `ConnectTargetAsync()`:
```csharp
// After target successfully connects
if (SelectedSourceConnection != null && !IsSourceConnected)
{
    if (SelectedSourceConnection.UseWindowsAuth || HasPasswordFor(SelectedSourceConnection))
    {
        await ConnectSourceAsync(SelectedSourceConnection);
    }
}
```

**Result**: User enters ONE password (target), source auto-connects using credentials from main app login. Seamless UX!

**Lesson**: Don't fight timing issues. Find the RIGHT trigger point (after user action completes).

---

## Current Issues (To Fix)

(None currently tracked)

---

## UI Color Scheme

**Two themes**: Dark (Ghostty Default Dark) in `Styles/AppTheme.axaml`, Light (warm cream) in `Styles/AppThemeLight.axaml`. See docs/DESIGN-SYSTEM.md for the full color spec.

Every AXAML file uses `{DynamicResource KeyName}`. ThemeManager.ApplyTheme() swaps the resource dictionary and fires `ThemeChanged` event. All code-behind that uses theme colors subscribes to this event and re-applies.

Key resources: `EditorBackground`, `ToolbarBackground`, `SidebarBackground`, `TitleBarBackground`, `PanelHeaderBackground`, `TextPrimary`, `TextSecondary`, `ButtonPrimary`, `ButtonDanger`, `ButtonSecondary`, `BorderDefault`, `AccentBlue`.

Syntax highlighting: `ThemeManager.cs` has both Dark and Light color sets. `ThemeManager.GetKeywordColor()` etc. return the correct color for the current theme.

**NEVER add a hardcoded `#xxxxxx` to any AXAML or code-behind file.** Add a resource to both AppTheme.axaml AND AppThemeLight.axaml, reference with `{DynamicResource}` in XAML, or use ThemeManager helpers in code-behind and subscribe to ThemeChanged.

---

## Testing Checklist
- [x] Connection persistence across app restarts
- [x] Theme switching (light/dark) on both tabs
- [x] Deploy to object that exists vs doesn't exist
- [x] Deploy with CREATE vs CREATE OR ALTER in source
- [x] Search with underscores and spaces
- [x] Three-way compare (Source → Target1 → Target2)
- [x] Auto-connect source after target connects (ONE password flow)
- [x] Batch selection and deploy
- [x] Unified search (name + code search with debounce)
- [x] Dependency explorer (Uses / Used By with chain navigation)
- [x] Sleep/wake recovery with reconnect overlay
- [x] Encrypted password persistence (survives restart)
- [x] Auto-sync timer (60s background polling)
- [x] Copy buttons on Version History diff panel
- [x] Keyboard shortcuts (Cmd/Ctrl+1/2/F/R/S/D, Escape)
- [x] Object Explorer right-click context menus (table, view, proc, function, column)
- [x] Object Explorer double-click quick actions (table, proc, column)
- [x] Multi-tab query editor (Ctrl+N new, Ctrl+W close, middle-click close)
- [x] Menu bar (File/Edit/Query/Help)
- [x] About dialog with version display
- [x] Settings dialog shows version at bottom
- [ ] Editable result grid: edit mode toggle on simple SELECT with PK
- [ ] Row-based change tracking (yellow=modified, green=new, red=deleted)
- [ ] Show SQL preview, Apply in transaction, Cancel discards
- [ ] "Edit Data" context menu on tables (SELECT TOP 200 + auto-edit-mode)
- [ ] Saved queries: Ctrl+S shows save dialog for new query, overwrites for existing
- [ ] Open query: Ctrl+O lists saved queries, double-click loads, Browse picks .sql file
- [ ] Save As: Ctrl+Shift+S always prompts for new name
- [ ] Recent Files menu populated and clickable
- [ ] Tab title shows query name + asterisk for unsaved changes
- [ ] Close tab "Save" button saves before closing

---

## Quick Reference

### Run the app
```bash
cd /Users/omer/Documents/Projects/SqlVersionControl
dotnet run
```

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| **Navigation** | |
| `Cmd/Ctrl+1` | Switch to Query Editor tab |
| `Cmd/Ctrl+2` | Switch to Version History tab |
| `Cmd/Ctrl+3` | Switch to Compare Databases tab |
| `Cmd/Ctrl+4` | Switch to Execution Plan tab |
| `Cmd/Ctrl+G` | Go to line number |
| `Escape` | Back from dependencies → clear search → deselect |
| **Query Tabs** | |
| `Ctrl+N` | New query tab |
| `Ctrl+W` | Close active query tab |
| `F5` | Run query |
| `Ctrl+Enter` | Run query |
| **File** | |
| `Cmd/Ctrl+O` | Open saved query |
| `Cmd/Ctrl+S` | Save query / Sync from DDL log (other tabs) |
| `Cmd/Ctrl+Shift+S` | Save As query |
| **Search** | |
| `Cmd/Ctrl+F` | Focus search box / Find in editor |
| `Cmd/Ctrl+H` | Replace in editor |
| `Cmd/Ctrl+R` | Refresh |
| `Cmd/Ctrl+D` | Dependencies for selected object |
| **Editor** | |
| `Cmd+K` / `Ctrl+K` | Comment selected lines (`--` prefix) |
| `Cmd+L` / `Ctrl+L` | Uncomment selected lines (remove `--`) |
| `Cmd+Shift+U` / `Ctrl+Shift+U` | Uppercase selection |
| `Cmd+Shift+L` / `Ctrl+Shift+L` | Lowercase selection |
| `Alt+Up` / `Alt+Up` | Move line up |
| `Alt+Down` / `Alt+Down` | Move line down |
| `Option+Z` / `Alt+Z` | Toggle word wrap |
| `Cmd+Shift+Z` / `Ctrl+Y` | Redo |
| **Tools** | |
| `Ctrl+Shift+F` | Format SQL (selection or all) |
| `Ctrl+Shift+Q` | Quick quote selection |
| `Cmd+Click` / `Ctrl+Click` | Peek definition (proc/function/view) |
| **Results Grid** | |
| `Cmd+Shift+C` / `Ctrl+Shift+C` | Copy with column headers |


### Other UI
- Settings: Gear icon (top right)
- Change DB: "Change DB" button in toolbar

### Data storage
- Settings: `~/Library/Application Support/Lookout/settings.json` (macOS)
- Passwords: `~/Library/Application Support/Lookout/credentials.json` (macOS) — encrypted at rest (DPAPI on Windows, AES on macOS)
- Saved queries: `~/Library/Application Support/Lookout/queries/*.sql` (macOS) — plain .sql with metadata header

---
## END OF Lookout PROJECT DOCUMENTATION
---
