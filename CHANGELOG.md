# Lookout — Changelog

All version history in reverse chronological order.

---

## v2.6.7 (April 2026)
- Toad-style cell editing: smooth double-click-to-edit, no column rebuild or visual flash
- Double-click in edit mode directly opens that cell for editing
- Escape cancels cell edit only; second Escape exits edit mode
- Tab/Enter commit cell and navigate (next cell / next row)
- Columns use TwoWay binding always; NULLs styled via LoadingRow
- Drag-and-drop .sql files from Finder/Explorer onto editor opens them in new tabs
- Uncomment (Cmd+L) now works on indented lines (skips leading whitespace)

## v2.6.6
- Fix: Windows menu bar (File/Edit/View/Tools/Help) unresponsive — BeginMoveDrag on TitleBarBorder was stealing pointer from menu items; now macOS-only since Windows has its own title bar
- Cell detail panel updates on every cell click (not just row changes)
- Detail panel resize eats into results grid height so it works even when results fill the screen
- Editor row uses Star layout so detail panel doesn't leave gaps below

## v2.6.5
- Results grid auto-sizes to fit content, capped at 50% of editor area (min 20%)
- Cell detail panel moved to own row below results (no longer overlays grid)
- Double-click splitter toggles between auto-sized and 50/50 maximized
- Cmd/Ctrl+R toggles results panel visibility (was Refresh, now uses OE button)
- Grid vertical line opacity increased from 0.2 to 0.6 for better column separation
- Fix: quick-switch to already-connected server now loads databases in new tab
- Height calculation includes buffer row to prevent last-row clipping

## v2.6.4
- New query tabs (+ button, Ctrl+N) inherit the active tab's connection instead of defaulting to primary
- Refresh button (↻) in Object Explorer header reloads databases from server
- Center-aligned OE header button icons

## v2.6.3
- Pin Test/Connect/Disconnect buttons below scroll area in Connection Manager (always visible)
- All action buttons on one row: Test/Connect/Disconnect left, Cancel/Save right
- Quick-switch buttons update immediately on connect/disconnect (registry event subscription)
- Light theme: darken quick-switch button text/borders for contrast on cream background
- Conditional multi-target: net9.0;net10.0 on SDK 10+, net9.0-only on SDK 9

## v2.6.2
- Context menu theme fix — global context menu dark theme styling

## v2.6.1
- OE click-to-expand: click anywhere on container row to expand/collapse (not just the arrow)

## v2.6.0
- MISC_v2: OE visual redesign, QoL features batch
- Empty state guidance for new users
- Tab drag reorder
- Command palette centered

## v2.5.1
- OE actions use correct connection/database for new tabs
- Tab dot+border fade on disconnect
- Tab tooltips show connection info
- Messages tab bar for DML/DDL

## v2.5.0
- 22 UX improvements from IMPROVEMENTS.md audit
- OE double-click for Views/Functions, Ctrl+Tab, ConnectOnStartup
- Edit mode confirm, DML messages tab, delete connection confirm
- Intellisense cache invalidation, Compare tab error surfacing
- History search debounce ObjectDisposedException fix

## v2.4.9
- Extended title bar (traffic lights) macOS-only — fixes Windows title bar buttons being cut off

## v2.4.8
- Fix crash on long-running queries — elapsed timer was updating UI from thread pool thread

## v2.4.7
- Fix Connection Manager button states — Connect/Disconnect always visible with proper enable/disable
- Connect auto-saves new connections

## v2.4.6
- OE connection context menu (New Query, Disconnect)
- Reconnect prompt on F5
- Session tab color preservation

## v2.4.5
- Fix macOS window position drift — all dialogs use CenterOwner instead of CenterScreen

## v2.4.4
- Live elapsed timer on status bar during query execution
- Connection dialog scrollable, password fix

## v2.4.3
- Connection dialog scrollable, password preserved on mode switch
- Command palette uppercase/lowercase fix

## v2.4.2
- Offline UX — Continue Offline on connection dialog, contextual error messages on F5, dynamic empty state

## v2.4.1
- Command Palette (Cmd+E) — VS Code-style fuzzy command search

## v2.4.0
- OE: Parameters under Procs/Functions (new `Parameter` node type, detailed type info with OUTPUT badge)
- OE: Columns under Views (expandable with same display as table columns)
- OE: Indexes under Tables (type, key columns, included columns from sys.indexes)
- OE: Foreign Keys under Tables (referenced table, column mapping, cascade actions)
- OE: Constraints under Tables (CHECK expressions, DEFAULT values)
- OE: User-Defined Types top-level folder (scalar + table types)
- OE: Database-Level DDL Triggers folder (name, enabled/disabled, event types)
- OE: Show Dependencies right-click on Proc/Function/View/Trigger — replaces tree with Uses/Used By, chain navigation, Back button
- Executed Selection Flash: 300ms blue highlight on F5'd selection range
- Crash Reporter: CrashLogger service, global exception handlers, structured crash logs, red banner on next startup
- View menu: Toggle OE (Ctrl+B), Toggle Results (Ctrl+J), Zoom In/Out/Reset, Toggle Theme, Word Wrap
- Edit menu: added Go to Line (Cmd+G), Select All (Cmd+A)
- Cmd/Ctrl+Mouse Wheel zoom on editor (8-32 range, persists to settings)
- Editor selection colors: themed SelectionBrush + SelectionForeground
- Editor right-click context menu: Cut/Copy/Paste, Format SQL, Comment/Uncomment, Upper/Lowercase, Quick Quote, Go to Line, Find/Replace, contextual Peek Definition/Quick Execute/Show Dependencies

## v2.3.0
- Query Trace Mode 1 — Quick Trace (Ctrl+Shift+F5): run a query with XE tracing, see every internal statement with duration/CPU/reads
- Query Trace Mode 3 — Capture (Ctrl+6): top-level Trace tab, Profiler replacement with filter setup, start/stop recording, searchable results grid
- TraceService: XE session lifecycle, permission checking, orphaned session cleanup on startup
- Toolbar "Trace" button next to Run/Stop
- Status bar Ln/Col cursor position indicator
- Cmd+=/- font zoom (persists to settings)
- Cmd+Shift+T instant dark/light theme toggle
- Window title shows active database ("Lookout — PROD WMS / GratisWMS")
- Tab right-click context menu: Close, Close Others, Close Right, Close All, Duplicate Tab
- Database dropdown preserves selection across tab switches and async loads
- OE TreeView: horizontal scroll disabled, no layout shift on selection
- Toolbar buttons tightened (22px height, MinHeight=0)

## v2.2.0
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

## v2.1.2
- Reconnect overlay: Dismiss button + Escape key, background retry every 10s, offline status bar (grey dot, desaturated stripe, "(offline)" suffix)
- Compare scan: Cancel button with CancellationToken, shows partial results on cancel
- Batch deploy: Cancel button, per-object progress ("Deploying 3/17: usp_GetStock..."), explicit 30s CommandTimeout
- Git Export cancel: verified already working
- Window title updated to "Lookout" in all states

## v2.1.1
- Security: Connection string building now uses SqlConnectionStringBuilder (fixes injection via semicolons in passwords)
- Security: TrustServerCertificate now configurable per-connection (default true, prep for public release)
- Security: Single-quote escaping for DB_ID() in index analysis queries
- Security: DDL audit table source bracket-escaped + Settings input validation
- Quality: Application-level file logger (AppLogger) replaces all Console.WriteLine — writes to logs/app.log with 5MB rotation
- Quality: RollbackToVersionAsync now returns actual error messages
- Quality: ConvertToCreateOrAlter deduplicated (single source of truth in DatabaseService)
- Quality: ActivityViewModel implements IDisposable, disposed on app close
- Quality: GetTableStructureAsync made static
- Quality: CancellationTokenSource properly disposed before reassignment
- Quality: Schedule removal now has confirmation dialog
- Quality: AlterSequenceDialog shows warning about duplicate keys / skipped ranges
- Quality: SPID re-fetched each refresh cycle
- Quality: PasswordStore uses ConcurrentDictionary for thread safety
- Quality: FormatValue handles numeric types explicitly, unsupported types get /* comment */ fallback
- Quality: Auto-sync timer stopped on app close

## v2.1.0
- App renamed to "Lookout" — all user-facing surfaces updated, config folder auto-migrated
- Quick Execute (Shift+Click) — click a proc name to open a new tab with ready-to-run EXEC template with typed parameters

## v2.0.0
- Git Export (File → Export to Git / Settings → Export Now) — full snapshot export of all database objects as .sql files, change detection, cleanup of deleted objects, CHANGELOG.md generation, progress dialog with summary
- Activity Monitor tab (Cmd+5) — real-time server monitoring with two sub-tabs:
  - Sessions: sp_who replacement with DMV queries, blocking chain visualization, Kill Session with safety checks, auto-refresh, filters
  - Jobs Dashboard: full SQL Agent job monitoring with color-coded status, human-readable schedules, inline job property editor (General/Steps/Schedule/History tabs), Start/Stop/Enable/Disable actions
- AccentGreen theme resource added to both themes

## v1.9.0
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

## v1.8.4
- Query Formatter (Ctrl+Shift+F) — T-SQL formatting via Hogimn.Sql.Formatter, toolbar F button
- Text Compare dialog (Tools → Text Compare) — reuses DiffView component
- Dialog base styling — unified background, fonts, inputs, button padding across all 11 dialogs
- Toolbar separator between Run/Stop and utility buttons

## v1.8.3
- Tools menu with SQL Quoter dialog (paste values, get IN clause output)
- Quick Quote toolbar button (`"` icon, Ctrl+Shift+Q) — quotes selected text in-place
- Script Object As context menus (CREATE, ALTER, DROP, INSERT for tables; ALTER/DROP for procs/views/functions)
- Peek Definition (Cmd+Click / Ctrl+Click on proc/view/function names)
- Highlight all occurrences of selected word (case-insensitive, whole-word)
- Move line up/down (Alt+Up/Down)
- Go to Line (Cmd+G / Ctrl+G)
- Redo keybinding fix (Cmd+Shift+Z / Ctrl+Y)
- Context menu styling (12px font, themed background)

## v1.8.2
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
- DDL audit source configurable in Settings
- Git integration path in Settings
- Closed-eye logo (monochrome, line-art, works at all sizes)
- Tooltips on all toolbar buttons with keyboard shortcuts
- CI fix: publish with `-f net9.0` flag

## v1.8.0
- Light theme, design system overhaul, Settings Phase 3

## v1.7.0
- Collapsible panels, Sequences in OE, Table Structure Compare, SQL Agent Jobs

## v1.6.0
- Per-tab connections + quick-switch buttons

## v1.5.0
- Performance, session, connections & data tools

## v1.4.0
- Menu bar, multi-tab query editor, editable grid, saved queries

## v1.3.0
- Execution Plan tab

## v1.2.0
- Unified search, dependency explorer, sleep/wake recovery, encrypted passwords, auto-sync
