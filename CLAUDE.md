# Lookout — SQL Server Desktop IDE

---
## PROJECT STATUS: v2.15.0 (April 2026)

BackgroundPollManager pauses Activity auto-refresh on tab/window inactive (eliminates macOS "significant energy" flag). Trace recording (NeverGated) keeps draining XE buffer always. Status bar poll indicator (idle/polling/recording). Editor crash fix (VisualLinesInvalidException on Option+Shift+drag — pointer block + dispatcher filter). Row-header Cmd+C copies all columns (branch reorder + ResolveCopyColumnRange helper). Right-click Copy no longer includes headers. Option+Drag column (rectangle) selection. Removed Shift+Click Quick Execute.

See [CHANGELOG.md](CHANGELOG.md) for full version history.

---

## Project Identity
- **Project Name**: Lookout
- **Folder**: `/Users/omer/Documents/Projects/SqlVersionControl`
- **Repository**: omervaner/SqlVersionControl
- **Purpose**: Cross-platform SQL Server desktop IDE

## What This App Does

### Query Editor
Multi-tab SQL editor with Object Explorer, results grid, editable grid (TOAD-style row editing with PK-based DML), execution plans, saved queries, drag-and-drop, intellisense, GO batch splitting, and SQL Agent job management in Object Explorer.

### Version History
Tracks DDL changes to procs/functions/views/triggers over time via audit log sync. Side-by-side diff, rollback, dependency explorer, unified search (name + code).

### Database Compare
Compare objects between 2–3 databases, deploy changes with `CREATE OR ALTER`. Code, table structure, and data comparison modes. Database dropdown on each connection for switching without reconnecting.

### Activity & Jobs
Server health dashboard (CPU, memory, sessions, blocking chains). Jobs tab with stat cards, enabled/disabled split, detail panel, start/stop/enable/disable controls.

### Execution Plan
Estimated plan visualization with cost breakdown bar, human-readable operator labels, code-to-plan linking, warnings, missing index suggestions. Uses PlanViewer.Core submodule (DO NOT MODIFY `lib/PerformanceStudio/`).

## Tech Stack
- **Framework**: Avalonia UI 11.x (.NET 9)
- **Pattern**: MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **SQL**: Microsoft.Data.SqlClient
- **Diff Engine**: DiffPlex
- **Platforms**: macOS (ARM64), Windows (x64)

## Project Structure
```
SqlVersionControl/
├── Views/           - Avalonia XAML views and code-behind
├── ViewModels/      - MVVM view models
├── Models/          - Data models (ConnectionSettings, ObjectVersion, QueryResult, EditableRow, etc.)
├── Services/        - DatabaseService, SettingsService, ThemeManager, DataEditService, etc.
├── Styles/          - AppTheme.axaml (dark), AppThemeLight.axaml (warm cream light)
├── Assets/          - Logo SVGs, backup icons
├── scripts/         - Docker seed scripts (seed-server1.sql, seed-server2.sql)
├── docs/            - Reference docs
│   ├── SHORTCUTS.md              - All keyboard shortcuts
│   ├── SESSION-2026-04-05.md     - Session task plans
│   ├── HISTORY-GIT-INTEGRATION.md
│   ├── SERVER-HEALTH.md
│   └── archived/                 - Legacy design docs
├── lib/             - Git submodule: PlanViewer.Core (DO NOT MODIFY)
├── CLAUDE.md        - This file (developer guide)
├── CHANGELOG.md     - Full version history
└── SqlVersionControl.csproj
```

---

## THE #1 RULE — SINGLE SOURCE OF TRUTH

**If you are about to write the same logic in a second place, STOP. Make it a method and call it from both places.**

Duplicated logic with slight variations is the #1 source of bugs. Every time code gets copy-pasted and modified, the copies drift apart.

**Real examples that caused painful bugs:**
- DataGrid column building copy-pasted into 3 places — NULL display broke in edit mode
- Database list population duplicated across 3 ViewModels — dropdowns went blank
- Syntax highlighting loaded 2 different ways — keywords were unreadable for weeks

**Before writing ANY code, ask: does this logic already exist somewhere? If yes, extract it into a shared method.**

---

## Critical Architecture Patterns

### 1. Settings Sharing (IMPORTANT!)
`SettingsService` must be a SINGLE shared instance across all views. Pass from MainWindow to all dialogs and views. Previously, CompareViewModel created its own instance, breaking connection persistence on Windows.

### 2. Auto-Connect Source After Target Connects
In `ConnectTargetAsync`, after target connects, auto-connect source using stored credentials from main app login. User enters ONE password (target), source connects silently.

### 3. Deploy Script Conversion
**ALWAYS** convert `CREATE` to `CREATE OR ALTER` for idempotent deploys. Used in: `CompareViewModel.DeployAsync()`, `DeploySelectedAsync()`, `DatabaseService.RollbackToVersionAsync()`.

### 4. Theme-Aware Dynamic Resources
**NEVER add hardcoded `#xxxxxx`** to any AXAML or code-behind file. Add resources to both `AppTheme.axaml` AND `AppThemeLight.axaml`, use `{DynamicResource}` in XAML or ThemeManager helpers in code-behind.

### 5. New Features Go in ViewModels — Not Code-Behind
All new logic goes in ViewModels with `[RelayCommand]` and `[ObservableProperty]`. Code-behind only for pure UI concerns (focus, visual tree, drag-drop). Existing code-behind stays as-is (migration too risky).

### 6. Results Grid — Custom Cell Selection (IMPORTANT!)

Avalonia's DataGrid does NOT support click-drag selection or column-scoped highlighting natively. We implemented both from scratch. **Do not modify this code without understanding the full system.**

**Key files:**
- `QueryTabView.Results.cs` — `RepaintCellSelection()`, `ApplyCellSelectionToRow()`, `WireDragSelection()`, drag handlers, `GetColIndexAtPoint()`
- `QueryTabView.EditMode.cs` — `OnResultsGridKeyDown()` (Cmd+C copy uses column range)
- `QueryTabView.Export.cs` — `CopyWithHeadersAsync()` (respects column range)
- `QueryTabView.axaml` — Inline styles: `DataGridRow:selected` transparent background

**What will break if you're not careful:**
- Removing the `DataGridRow:selected` transparent style → full-row blue highlight returns
- Changing `CellPointerPressed` handler → can reset `_dragStartColIndex`/`_dragEndColIndex` and break multi-column selection
- Adding `PointerReleased` handlers that don't filter by button → right-click will reset drag state
- Modifying `BuildColumnsForGrid` without preserving `CellStyleClasses` → numeric right-alignment breaks
- Removing the `.Clone()` in `EnterEditModeAsync` → cancel/discard edits will mutate original Results

---

## Build & Release

### Debug Build
```bash
dotnet build -f net10.0    # or net9.0 on machines without .NET 10
dotnet run -f net10.0
```

### IMPORTANT: Multi-Target & Velopack Release Process

The csproj targets **both net9.0 and net10.0** (`<TargetFrameworks>net9.0;net10.0</TargetFrameworks>`).
- **Always publish with `-f net9.0`** for releases — ensures the binary works on both.
- **Always run debug builds with `-f net10.0`** on the home Mac.
- The `vpk` CLI (Velopack v0.0.1298) targets .NET 9. On .NET 10 machines: `DOTNET_ROLL_FORWARD=LatestMajor`.
- **Do NOT use `vpk upload github`** — use `gh release create` instead.

### Full Release Checklist
```bash
# 1. Bump version in SqlVersionControl.csproj (<Version>X.Y.Z</Version>)
# 2. Update CLAUDE.md project status header + CHANGELOG.md

# 3. Commit and push
git add <files>
git commit -m "vX.Y.Z — description"
git push origin main

# 4. Publish (ALWAYS use -f net9.0 for releases)
dotnet publish -c Release -r osx-arm64 --self-contained -f net9.0 -o publish/osx-arm64

# 5. Velopack package
DOTNET_ROLL_FORWARD=LatestMajor vpk pack \
  --packId Lookout --packVersion X.Y.Z \
  --packDir publish/osx-arm64 --mainExe SqlVersionControl \
  --icon AppIcon.icns --outputDir Releases

# 6. Create GitHub release
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

## UI Color Scheme

**Two themes**: Dark in `Styles/AppTheme.axaml`, Light (warm cream) in `Styles/AppThemeLight.axaml`.

Every AXAML file uses `{DynamicResource KeyName}`. `ThemeManager.ApplyTheme()` swaps the resource dictionary and fires `ThemeChanged`. Key resources: `EditorBackground`, `ToolbarBackground`, `SidebarBackground`, `TextPrimary`, `TextSecondary`, `ButtonPrimary`, `ButtonDanger`, `BorderDefault`, `AccentBlue`, `CellSelectionHighlight`, `CellSelectionBorder`.

---

## Quick Reference

### Run the app
```bash
cd /Users/omer/Documents/Projects/SqlVersionControl
dotnet run -f net10.0
```

### Keyboard shortcuts
See [docs/SHORTCUTS.md](docs/SHORTCUTS.md) for the full list.

### Data storage
- Settings: `~/Library/Application Support/Lookout/settings.json` (macOS)
- Passwords: `~/Library/Application Support/Lookout/credentials.json` (macOS) — encrypted (DPAPI on Windows, AES on macOS)
- Saved queries: `~/Library/Application Support/Lookout/queries/*.sql` (macOS)

---
## END OF Lookout PROJECT DOCUMENTATION
---
