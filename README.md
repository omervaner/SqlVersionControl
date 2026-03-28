# SQL Version Control

A cross-platform SQL Server desktop IDE for teams that need more than SSMS but less than a full DevOps pipeline. Track DDL changes, write and run queries, compare databases across environments, visualize execution plans, and manage SQL Agent Jobs — all from one app.

Built with [Avalonia UI](https://avaloniaui.net/) and .NET 9. Runs on macOS and Windows.

---

## Features

### Query Editor
A multi-tab SQL editor with everything you'd expect from a daily-driver query tool:

- **Multi-tab interface** — Ctrl+N for new tabs, each with its own database connection. Tab 1 on DEV, Tab 2 on PROD, simultaneously.
- **Object Explorer** — Collapsible tree browser with tables, views, stored procedures, functions, sequences, and SQL Agent Jobs. Right-click any object for contextual actions: Script as CREATE, SELECT TOP 100, SELECT COUNT(*), View Definition, Generate EXEC with parameter placeholders, SELECT DISTINCT on a column, Edit Data, and more. Double-click a table to instantly run SELECT TOP 100. Double-click a proc to view its definition. Drag-and-drop object names directly into the editor. Filter the tree by typing in the search box.
- **Run queries** — F5 or Ctrl+Enter to execute. Supports `GO` batch splitting, selected-text execution, and cancellation. Results in a tabbed grid with messages, row counts, and PRINT output.
- **Results grid right-click menu** — Select rows in the results grid and right-click to:
  - **Copy as INSERT** — Generates `INSERT INTO ... VALUES (...)` statements for the selected rows. Select 5 rows from a PROD table, copy as INSERT, paste into a UAT query tab, run. That's it — data moved between environments in seconds.
  - **Copy Selected Rows** — Tab-delimited copy, pastes cleanly into Excel or another grid.
  - **Export to Excel** — Export selected rows or the full result set to `.xlsx` with auto-sized columns and frozen headers.
- **Editable result grid** — For simple single-table SELECTs with a primary key, toggle edit mode to modify rows inline. Row-level change tracking with color coding (yellow = modified, green = new, red = deleted). Add new rows, mark rows for deletion, preview the generated DML, and apply everything in a single transaction. Right-click a table in Object Explorer and choose "Edit Data" to jump straight into edit mode with `SELECT TOP 200`.
- **Autocomplete** — Schema-aware intellisense for tables, columns, and keywords.
- **Saved queries** — Save, open, and organize `.sql` files. Recent files menu, tab titles reflect file names, unsaved changes marked with an asterisk.
- **Per-tab connections** — Each tab owns its connection. Named connections appear as colored buttons in the status bar for quick switching.

### Version History
Track changes to stored procedures, functions, views, and triggers over time. Syncs from a DDL audit log and stores every version.

- **Object browser** with version counts — see at a glance which objects changed and how many versions exist.
- **Side-by-side diff** — Compare any two versions with syntax highlighting and red/green line-level diffs. Copy buttons for both sides.
- **Rollback** — Restore any previous version with one click. Uses `CREATE OR ALTER` for safe execution.
- **Unified search** — Type to filter by object name instantly. After a short pause, searches inside definitions too (code search). Results marked with "(in code)" when matched inside the body.
- **Dependency explorer** — Select an object, click Dependencies, and see "Uses" / "Used By" chains with navigation.
- **Auto-sync** — 60-second background polling picks up new DDL changes without interrupting your workflow.

### Database Compare
Compare objects between two (or three) databases and deploy changes. Three comparison modes:

- **Code Compare** — Compare stored procedures, functions, views, and triggers across environments. Side-by-side diff view with syntax highlighting. Batch selection and deploy with `CREATE OR ALTER`. Optional third database for three-way comparison.
- **Table Structure Compare** — Column-level diffs showing type, nullability, and default constraint differences. Generates `CREATE TABLE` or `ALTER TABLE` DDL. Deploy individual columns or entire tables in a transaction.
- **Table Data Compare** — Row-level comparison of the same table across two environments. Pick a match key (defaults to PK, changeable to any business key), optionally filter with keyword search, and see which rows differ, which are missing, and which are extra. Equalize selected rows by generating INSERT/UPDATE statements wrapped in a transaction with preview before execution. Built for config and reference tables that drift between PROD/UAT/DEV.

### Execution Plan
Generate and visualize SQL Server estimated execution plans with human-readable explanations.

- **Cost breakdown bar** — Horizontal stacked bar showing operator costs proportionally, color-coded by type, clickable.
- **Operator tree** — Flattened tree with plain-English labels like "Reading entire table: Orders (slow - no filter used)" instead of raw operator names. Raw names available in tooltips for DBAs.
- **Code-to-plan linking** — Click an operator to highlight the corresponding SQL statement in the editor.
- **Warnings** — Surface plan warnings (implicit conversions, spills, etc.) in a dedicated panel.
- **Missing indexes** — Suggestions with a "Copy CREATE INDEX" button.

### SQL Agent Jobs
Browse and manage SQL Server Agent Jobs directly from the Object Explorer.

- **Jobs folder** under each database, lazy-loaded from `msdb`. Shows job name, enabled/disabled status, and last run outcome (Success/Failed/Running).
- **Context menu** — View Job Steps (shows step details with T-SQL), View History (last 10 runs with status, duration, and messages), Start Job (with confirmation dialog), and Refresh.

### Other
- **Dark and light themes** with live preview. All colors defined in a single theme file — change one value and the entire app updates.
- **Encrypted credential storage** — Passwords encrypted at rest (DPAPI on Windows, AES on macOS). Survives app restarts.
- **Sleep/wake recovery** — Detects when the machine wakes from sleep and reconnects gracefully.
- **Session restore** — Query tabs, connections, and editor contents are saved on exit and restored on next launch.
- **Auto-updates** — Powered by Velopack. The app checks for new releases on GitHub and updates itself.

---

## Install

Download the latest release from [Releases](https://github.com/omervaner/SqlVersionControl/releases).

The app supports auto-updates — once installed, it checks for new versions automatically and applies them on restart.

| Platform | File |
|----------|------|
| macOS (Apple Silicon) | `.pkg` installer or `.zip` portable |
| Windows (x64) | `.exe` installer |

---

## Build from Source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/omervaner/SqlVersionControl.git
cd SqlVersionControl
dotnet run
```

### Release Build

```bash
# macOS ARM64
dotnet publish -c Release -r osx-arm64 --self-contained -o publish/osx-arm64

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64
```

---

## Tech Stack

| | |
|---|---|
| **UI Framework** | [Avalonia UI](https://avaloniaui.net/) 11.x |
| **Runtime** | .NET 9 |
| **MVVM** | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| **SQL** | [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) |
| **Diff Engine** | [DiffPlex](https://github.com/mmanela/diffplex) |
| **Editor** | [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) |
| **Execution Plans** | [PlanViewer.Core](https://github.com/erikdarlingdata/PerformanceStudio) (MIT) |
| **Auto-Update** | [Velopack](https://velopack.io/) |
| **Excel Export** | [ClosedXML](https://github.com/ClosedXML/ClosedXML) |

