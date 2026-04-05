# Lookout UX Audit — Unhandled States & Confusing Flows

**Goal**: Catalog every place where a user might get stuck, confused about what's happening, or not know how to get out of a state.

---

## 🔴 Do First — Blocks Core Workflow

### 1. Tab Overflow Pushes Toolbar Off-Screen — DONE
**Where**: QueryEditorHost — tab strip and toolbar share the same row
**Problem**: When many tabs are open (8-10+), the tab strip grows indefinitely and pushes the Run/Stop/Trace buttons and Database dropdown off the right edge of the screen. The user literally cannot execute queries or change databases without closing tabs first.
**User experience**: "I have 10 tabs open and I can't see the Run button or the database dropdown anymore."
**Fix**: Single row with horizontal scroll — the proven pattern (VS Code, Chrome, IntelliJ). The tab strip scrolls with mouse wheel within its allocated space. An overflow indicator ("»" or "…" button) lists hidden tabs. The toolbar (Run, Stop, Trace, Database, Format, Quote) is always pinned to the right and never pushed off-screen.

Do NOT use two rows of tabs — it wastes vertical space permanently, creates confusion about which row is active, and still breaks at 20+ tabs.

---

## 🔵 Do Second — Consistent Connection Indicator

### 2. Standardized Connection Bar Across All Tabs — DONE
**Where**: Every non-Editor tab (History, Compare, Exec Plan, Activity, Trace)
**Problem**: Each tab handles its connection differently: History has a clickable button that looks like a label, Activity has a separate independent connection with no visual cue it's clickable, Exec Plan silently uses the main connection with no indicator, Trace has a ComboBox that only shows active connections, Compare has Source/Target dropdowns in their own style. Users can't tell at a glance which server each tab is talking to, and the interaction pattern to change it differs per tab.
**Desired behavior**: Every non-Editor tab shows a connection indicator in the same top-left position with the same visual style — colored dot + connection name + clickable to change.
- **History, Exec Plan, Activity**: Same indicator style, same position. Click opens connection picker.
- **Trace**: Same indicator style, replacing the current ComboBox that only shows active connections with no way to add new ones.
- **Compare**: Source and Target dropdowns should visually match the same indicator style (colored dot + name + dropdown) so they're recognizable as the same control, just with Source/Target label prefix.
- **Editor**: Exempt — uses per-tab connections with colored dots on the tab strip, which already works well.
- **Empty state**: When no connection is set, show guidance: "No connection — click to connect" or similar.

This single fix eliminates: Trace empty picker confusion, Exec Plan wrong connection, Activity independent connection confusion, connection buttons looking like labels, and Compare empty state when offline.

---

## 🔴 High Priority — User Gets Stuck or Confused

### 3. Object Explorer Search Doesn't Work on Collapsed Nodes — DONE
**Where**: ObjectExplorerViewModel.ApplyFilter()
**Problem**: The OE filter only walks nodes already loaded in memory. The tree is lazy-loaded — collapsed nodes contain a single dummy placeholder child. Searching for "t_customer" without first expanding the tree returns zero results.
**Desired behavior**: Query the server for matching objects across all connected databases, build a filtered tree showing only paths that contain results. Restore original tree when filter is cleared.
**Implementation notes**:
- New `DatabaseService.SearchObjectsAsync(connStr, filterText)` that queries `sys.objects` across databases
- Build path: Connection → Database → Category Folder → Object
- Use the save/restore pattern from dependency mode (`_savedNodes`)
- Only trigger server search after debounce (200ms, already exists) and when filter is 2+ characters
- Fall back to current client-side filter for already-expanded nodes

### 4. Execution Plan — Remove Top-Level Tab, Move Into Editor — DONE
**Where**: PlanView (currently a top-level tab), PlanViewModel, QueryEditorHost toolbar
**Problem**: The Exec Plan tab is coupled to Version History — you must select a proc there first, then switch to Exec Plan, then click Generate. No way to get a plan for the SQL you're actually writing. Also uses the wrong connection in multi-connection mode.
**Fix**: Remove the Exec Plan top-level tab entirely. Replace with two things:

**4A. Editor toolbar button (🔴 high priority)**:
Add an "Exec Plan" button next to Run/Stop/Trace in the editor toolbar (keyboard shortcut: Ctrl+L, matching SSMS). When clicked, runs `SET SHOWPLAN_XML ON` against the current SQL (selected text or full editor), parses the result, and shows it as a result tab called "Exec Plan" alongside Results and Messages. Uses the active tab's connection and database — zero ambiguity. The existing PlanView rendering (cost bar, operator tree, warnings, missing indexes) becomes a reusable component embedded in this result tab.

**4B. Tools menu standalone dialog (🟢 lower priority)**:
Add "Execution Plan Analysis" to the Tools menu. Opens a dialog with a proc/function search box (autocomplete from the connected server). Generates and displays the plan in a standalone window. Reuses the same PlanView component. This covers the "browse procs and compare plans" use case without requiring the Editor.

The existing PlanView.axaml and PlanViewModel become shared components used by both 4A and 4B rather than a top-level tab.

### 5. Version History — No Guidance When DDL Audit Table Missing — DONE
**Where**: MainWindowViewModel syncs from `VMAuditDb.dbo.DDL_Log`
**Problem**: On a fresh server without the DDL audit setup, Version History is empty with no explanation.
**Fix**: Show an empty state: "No version history found. This feature requires a DDL audit trigger. See Settings → DDL Audit Source to configure."

### 6. Edit Mode — No Escape Hatch When Connection Drops — DONE
**Where**: QueryTabView edit mode
**Problem**: If you enter edit mode, make changes, and the connection drops — you can't Apply and Cancel discards everything. No way to save your pending work.
**Fix**: Keep "Show SQL" working when disconnected (it generates SQL client-side). Add a "Copy SQL" button so user can save changes as a script.

### 7. Command Palette — "Run Query" and "Run with Trace" Are No-Ops — DONE
**Where**: MainWindow.BuildCommandRegistry()
**Problem**: Both entries have `Execute = () => { }`. Selecting them does nothing.
**Fix**: Wire them to `host?.RunActiveQuery()` and `host?.RunActiveWithTrace()`, or remove them from the palette.

---

## 🟡 Medium Priority — Confusing but Functional

### 8. Object Explorer — No Empty State When Disconnected — DONE
**Where**: QueryEditorHost OE tree
**Problem**: When all connections are disconnected, the OE is blank with no message.
**Fix**: Show "No active connections" with a "Connect..." button.

### 9. Reconnect — No Manual "Go Online" Button — DONE
**Where**: MainWindow reconnect flow
**Problem**: After dismissing the reconnect overlay, app enters offline mode with background retry every 10s. No way to manually trigger reconnect.
**Fix**: Make the "(offline)" status bar text clickable, or add a "Reconnect Now" button.

### 10. Trace Recording — No Warning on App Close — DONE
**Where**: MainWindow.OnClosing()
**Problem**: Closing the app while recording a trace orphans the XE session on the server.
**Fix**: Check `TraceViewModel.State == TraceState.Recording` in OnClosing and auto-stop the trace or warn.

### 11. Tab Reconnect — Only Available via Right-Click — DONE
**Where**: QueryEditorHost.BuildTabContextMenu()
**Problem**: When a tab's connection is lost (faded dot), reconnect is only available via right-click context menu. Not discoverable.
**Fix**: Make the faded dot clickable, or show an inline banner: "Connection lost. [Reconnect]"

### 12. Session Restore — Silent Connection Loss — DONE
**Where**: QueryEditorHost.RestoreSession()
**Problem**: If a saved connection was deleted, tabs silently lose their connection with no notification.
**Fix**: After restore, notify: "2 tabs could not reconnect — their saved connection no longer exists."

### 13. Three-Way Compare — "T2" Button Needs Tooltip — DONE
**Where**: CompareView "+T2" / "-T2" toggle
**Problem**: The button text is cryptic. No tooltip.
**Fix**: Add tooltip: "Add a second target database for three-way comparison." Consider renaming to "+ Target 2".

---

## 🟢 Low Priority — Polish

### 14. Query History — No Clear Option — DONE
**Where**: QueryEditorHost history panel
**Problem**: History grows indefinitely with no way to clear it.
**Fix**: Add a "Clear History" button with confirmation.

### 15. Intellisense — Silent Degradation — DONE
**Where**: QueryEditorHost.OnTabDatabaseChanged() — empty catch block
**Problem**: If schema loading fails, intellisense silently falls back to keywords-only.
**Fix**: Brief status message: "Schema loading failed — autocomplete limited to keywords."

### 16. Settings — No Indication of Immediate vs Deferred — DONE
**Where**: SettingsDialog
**Problem**: Some settings apply immediately, some need reconnect/sync. No visual distinction.
**Fix**: Subtle labels: "Takes effect immediately" vs "Applies on next sync."

### 17. Large Export — No Progress or Cancel — DONE
**Where**: QueryTabView.ExportResultsAsync()
**Problem**: Large exports happen synchronously with no progress indicator.
**Fix**: Progress dialog for exports over ~10K rows.

---

## Summary

| Priority | Total | Done | Remaining |
|----------|-------|------|-----------|
| 🔴 Do First | 1 | 1 | 0 |
| 🔵 Do Second | 1 | 1 | 0 |
| 🔴 High | 5 | 5 | 0 |
| 🟡 Medium | 6 | 6 | 0 |
| 🟢 Low | 4 | 4 | 0 |
| New | 1 | 1 (#18) | 0 |

**All 18 items completed.** UX audit is fully closed out.
**Completed**: #1, #2, #3, #4, #5, #6, #7, #8, #9, #10, #11, #12, #13, #14, #15, #16, #17, #18


### 18. Auto-Detect Environment from Connection Name
**Where**: Connection Manager — new connection creation
**Problem**: When creating a new connection, users manually pick a color and environment type every time. But the connection name almost always contains a hint.
**Fix**: On the name field's text change, pattern-match against common keywords (case-insensitive) and auto-set the color + environment:
- **prod, production, live, prd** → red, Environment = Production
- **dev, develop, development, local** → blue, Environment = Development
- **qa, uat, test, staging, stg** → yellow/orange, Environment = QA
- **No match** → keep current default (neutral grey, no environment)

Only auto-set if the user hasn't manually picked a color yet (track a `userPickedColor` flag that goes true on manual color selection, resets on name clear). This is a smart default, not forced — always overridable in the manager.
