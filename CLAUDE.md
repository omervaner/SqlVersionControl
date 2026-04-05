# Lookout — SQL Server Desktop IDE

---
## PROJECT STATUS: v2.12.0 (April 2026)

Compare tab database dropdowns: switch databases on any connected server without re-connecting. Settings admin/normal mode: hides DDL audit and Git export config from normal users. Job detail panel close button. Dev environment seed scripts for two-server Docker setup.

See [CHANGELOG.md](CHANGELOG.md) for full version history.

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
- **Drag-and-drop .sql files** from Finder/Explorer onto editor opens them in new tabs
- **Editable result grid** (TOAD-style): For simple single-table SELECTs with a PK, "Edit" button appears on result tab header. Toggle enters edit mode — DataGrid becomes writable. Row-based change tracking: snapshot on row enter, compare on row leave, Escape reverts. Yellow=modified, green=new, red=deleted. "Mark for Delete" via right-click. "Add Row" for inserts. "Show SQL" previews parameterized DML. "Apply" executes in a single transaction with row-count verification. "Edit Data" context menu on tables auto-runs SELECT TOP 200 and enters edit mode. Smooth double-click-to-edit cells, Tab/Enter to navigate, Escape to cancel.

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
├── CHANGELOG.md     - Full version history
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

## THE #1 RULE — SINGLE SOURCE OF TRUTH

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

### 5. New Features Go in ViewModels — Not Code-Behind

Historically, most logic ended up in code-behind files (`.axaml.cs`) while ViewModels stayed thin. We're not migrating existing code (too risky, not worth it), but **all new features must follow proper MVVM**:

- **New logic goes in ViewModels**: commands, state management, data operations, service calls.
- **Code-behind only for pure UI concerns**: focus management, visual tree manipulation, drag-and-drop wiring, things that genuinely can't be done via XAML bindings.
- **Use `[RelayCommand]` and `[ObservableProperty]`** from CommunityToolkit.Mvvm — don't wire `.Click += lambda` for new features.
- **Use data bindings** instead of `FindControl<T>()` and direct property sets.

This means over time, the ratio of ViewModel logic to code-behind logic shifts naturally. No big-bang migration, no risk, new code is just cleaner and testable.

```csharp
// BAD (old pattern — don't do this for NEW features):
// In SomeView.axaml.cs:
SomeButton.Click += async (_, _) => {
    var result = await _db.DoSomethingAsync();
    StatusLabel.Text = result;
};

// GOOD (new pattern — all new features like this):
// In SomeViewModel.cs:
[RelayCommand]
private async Task DoSomethingAsync()
{
    var result = await _db.DoSomethingAsync();
    StatusText = result; // [ObservableProperty] bound in XAML
}
```

---

## Build & Release

### Debug Build
```bash
dotnet build -f net10.0    # or net9.0 on machines without .NET 10
dotnet run -f net10.0
```

### IMPORTANT: Multi-Target & Velopack Release Process

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

## UI Color Scheme

**Two themes**: Dark (Ghostty Default Dark) in `Styles/AppTheme.axaml`, Light (warm cream) in `Styles/AppThemeLight.axaml`. See docs/DESIGN-SYSTEM.md for the full color spec.

Every AXAML file uses `{DynamicResource KeyName}`. ThemeManager.ApplyTheme() swaps the resource dictionary and fires `ThemeChanged` event. All code-behind that uses theme colors subscribes to this event and re-applies.

Key resources: `EditorBackground`, `ToolbarBackground`, `SidebarBackground`, `TitleBarBackground`, `PanelHeaderBackground`, `TextPrimary`, `TextSecondary`, `ButtonPrimary`, `ButtonDanger`, `ButtonSecondary`, `BorderDefault`, `AccentBlue`.

Syntax highlighting: `ThemeManager.cs` has both Dark and Light color sets. `ThemeManager.GetKeywordColor()` etc. return the correct color for the current theme.

**NEVER add a hardcoded `#xxxxxx` to any AXAML or code-behind file.** Add a resource to both AppTheme.axaml AND AppThemeLight.axaml, reference with `{DynamicResource}` in XAML, or use ThemeManager helpers in code-behind and subscribe to ThemeChanged.

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
| `Cmd/Ctrl+3` | Switch to Compare tab |
| `Cmd/Ctrl+4` | Switch to Activity tab |
| `Cmd/Ctrl+5` | Switch to Trace tab |
| `Cmd/Ctrl+E` | Command Palette |
| `Cmd/Ctrl+B` | Toggle Object Explorer |
| `Cmd/Ctrl+J` | Toggle results panel |
| `Escape` | Back / clear search / deselect / cancel query |
| **Query Tabs** | |
| `Cmd/Ctrl+N` | New query tab |
| `Cmd/Ctrl+W` | Close active query tab |
| `Cmd/Ctrl+Tab` | Next query tab |
| `Cmd/Ctrl+Shift+Tab` | Previous query tab |
| `F5` | Run query |
| `Cmd/Ctrl+Enter` | Run query |
| `Cmd/Ctrl+Shift+F5` | Run query with Trace |
| `Cmd/Ctrl+L` | Estimated execution plan |
| **File** | |
| `Cmd/Ctrl+O` | Open saved query |
| `Cmd/Ctrl+S` | Save query |
| `Cmd/Ctrl+Shift+S` | Save As query |
| **Search** | |
| `Cmd/Ctrl+F` | Find in editor / focus search |
| `Cmd/Ctrl+H` | Replace in editor |
| `Cmd/Ctrl+R` | Toggle results panel |
| `Cmd/Ctrl+D` | Dependencies for selected object |
| **Editor** | |
| `Cmd/Ctrl+K` | Comment selected lines |
| `Cmd/Ctrl+Shift+K` | Uncomment selected lines |
| `Cmd/Ctrl+Shift+U` | Uppercase selection |
| `Cmd/Ctrl+Shift+L` | Lowercase selection |
| `Alt+Up` | Move line up |
| `Alt+Down` | Move line down |
| `Option/Alt+Z` | Toggle word wrap |
| `Cmd+Shift+Z` / `Ctrl+Y` | Redo |
| `Cmd/Ctrl+Space` | Code completion |
| `Cmd/Ctrl+G` | Go to line number |
| `Cmd/Ctrl+Click` | Peek definition |
| `Shift+Click` | Generate EXEC (stored procedures) |
| **Tools** | |
| `Cmd/Ctrl+Shift+F` | Format SQL |
| `Cmd/Ctrl+Shift+Q` | Quick quote selection |
| `Cmd/Ctrl+Shift+T` | Toggle dark/light theme |
| `Cmd/Ctrl+Shift+?` | Keyboard shortcuts |
| `Cmd/Ctrl+=` | Zoom in |
| `Cmd/Ctrl+-` | Zoom out |
| **Results Grid** | |
| `Cmd/Ctrl+C` | Copy cell value |
| `Cmd/Ctrl+Shift+C` | Copy row with column headers |


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
