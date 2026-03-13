# CheatTeam - SQL Version Control Tool

---
## PROJECT STATUS: v1.4.0 (March 2026)
Menu bar (Task 4), multi-tab query editor (Task 4), version display (Task 4), Query Editor tab (Task 1), Object Explorer (Task 2), and Object Explorer context menus + quick actions (Task 3) added. Execution Plan tab shipped in v1.3.0. Unified search, dependency explorer, sleep/wake recovery, encrypted passwords, auto-sync from v1.2.0.
---

## Project Identity
- **Project Name**: CheatTeam (SQL Version Control)
- **Folder**: `/Users/omer/Documents/Projects/CheatTeam`
- **Repository**: omervaner/SqlVersionControl
- **Purpose**: Cross-platform desktop app for tracking DDL changes in SQL Server databases

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
- **Object Explorer context menus** (right-click): Table → SELECT TOP 100, SELECT COUNT(*), Script as CREATE; View → SELECT TOP 100, View Definition; Proc → View Definition, Generate EXEC with param placeholders; Function → View Definition; Column → SELECT DISTINCT, Insert Column Name
- **Double-click quick actions**: Table → SELECT TOP 100 (auto-run); Proc → View Definition; Column → Insert column name at cursor

### Menu Bar (v1.4.0)
Traditional menu bar (File, Edit, Query, Help) inside the dark titlebar, above the app tab row:
- **File**: New Query (Ctrl+N), Open/Save/SaveAs (stubs), Exit
- **Edit**: Undo, Redo, Cut, Copy, Paste, Find (Ctrl+F), Replace (Ctrl+H) — pass-through to active tab's AvaloniaEdit
- **Query**: Run (F5), Stop, Change Database
- **Help**: About (shows version dialog), Check for Updates (opens GitHub releases)

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
- **Pattern**: MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **SQL**: Microsoft.Data.SqlClient
- **Diff Engine**: DiffPlex for side-by-side comparison
- **Platforms**: macOS (ARM64), Windows (x64)

## Project Structure
```
CheatTeam/
├── Views/           - Avalonia XAML views and code-behind
├── ViewModels/      - MVVM view models
├── Models/          - Data models (ConnectionSettings, ObjectVersion, QueryResult, etc.)
├── Services/        - DatabaseService, SettingsService, ThemeManager, PlanTranslator, PlanXmlHelper
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
| `AppVersion.cs` | Static version string helper (reads from assembly) |

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
dotnet build
dotnet run
```

### Release Builds
```bash
# macOS ARM64
dotnet publish -c Release -r osx-arm64 --self-contained -o publish/osx-arm64

# Windows single file
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64-single
```

### macOS App Bundle
```bash
mkdir -p publish/SqlVersionControl.app/Contents/{MacOS,Resources}
cp -r publish/osx-arm64/* publish/SqlVersionControl.app/Contents/MacOS/
cp AppIcon.icns publish/SqlVersionControl.app/Contents/Resources/
codesign --force --deep --sign - publish/SqlVersionControl.app
hdiutil create -srcfolder publish/SqlVersionControl.app -volname "SQL Version Control" -format UDZO publish/SqlVersionControl-macOS.dmg
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

### Fixed Colors (Both Themes)
- Top/bottom bars: `#1a1a2e` (dark purple)
- Target2 bar: `#252540`
- Accent buttons: `#4a4a6e`
- Deploy green: `#2a6e4e`
- Deploy orange: `#e67e22`
- Target2 blue: `#2980b9`
- Danger red: `#e63946`

### Theme-Adaptive (Dynamic Resources)
- Panel backgrounds: `SystemControlBackgroundChromeMediumLowBrush`
- Headers: `SystemControlBackgroundChromeMediumBrush`
- Borders/splitters: `SystemControlForegroundBaseMediumLowBrush`
- Secondary text: `SystemControlForegroundBaseMediumBrush`

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

---

## Quick Reference

### Run the app
```bash
cd /Users/omer/Documents/Projects/CheatTeam
dotnet run
```

### Keyboard shortcuts
| Shortcut | Action |
|---|---|
| `Cmd/Ctrl+1` | Switch to Query Editor tab |
| `Cmd/Ctrl+2` | Switch to Version History tab |
| `Cmd/Ctrl+3` | Switch to Compare Databases tab |
| `Cmd/Ctrl+4` | Switch to Execution Plan tab |
| `Ctrl+N` | New query tab |
| `Ctrl+W` | Close active query tab |
| `F5` | Run query (Query Editor tab) |
| `Ctrl+Enter` | Run query (Query Editor tab) |
| `Cmd/Ctrl+F` | Focus search box / Find in editor |
| `Cmd/Ctrl+H` | Replace in editor (Query Editor tab) |
| `Cmd/Ctrl+R` | Refresh |
| `Cmd/Ctrl+S` | Sync from DDL log |
| `Cmd/Ctrl+D` | Dependencies for selected object |
| `Escape` | Back from dependencies → clear search → deselect |

### Other UI
- Settings: Gear icon (top right)
- Change DB: "Change DB" button in toolbar

### Data storage
- Settings: `~/Library/Application Support/SqlVersionControl/settings.json` (macOS)
- Passwords: `~/Library/Application Support/SqlVersionControl/credentials.json` (macOS) — encrypted at rest (DPAPI on Windows, AES on macOS)

---
## END OF CheatTeam PROJECT DOCUMENTATION
---
