# Lookout UX Audit — Unhandled States & Confusing Flows

**Goal**: Catalog every place where a user might get stuck, confused about what's happening, or not know how to get out of a state.

**Priority levels**: 🔴 User gets stuck / loses work, 🟡 Confusing but workarounds exist, 🟢 Polish / nice-to-have

---

## 🔴 High Priority — User Gets Stuck or Confused

### 1. Object Explorer Search Doesn't Work on Collapsed Nodes
**Where**: ObjectExplorerViewModel.ApplyFilter()
**Problem**: The OE filter only walks nodes already loaded in memory. The tree is lazy-loaded — collapsed nodes contain a single dummy placeholder child, not real objects. So if you type "t_customer" without first expanding PROD → AAD → Tables, the filter sees no children, hides the entire branch, and shows "No results."
**User experience**: "I searched for t_customer and got nothing. But it's right there when I expand Tables manually."
**Current behavior**: `ApplyFilterToNode()` recurses through `node.Children`, but for unexpanded Database/Folder nodes, `Children` is `[DummyNode]` — no real objects to match against.
**Desired behavior**: When the user types a search query (after debounce), query the server for matching objects across all connected databases (e.g. `SELECT name FROM sys.objects WHERE name LIKE '%filter%'`). Build a filtered tree showing only the paths that contain results: `PROD → AAD → Tables → t_customer`. Restore the original tree when the filter is cleared.
**Implementation notes**:
- Need a new `DatabaseService` method: `SearchObjectsAsync(connStr, filterText)` that queries `sys.objects` + `sys.procedures` etc. across all databases on that connection
- For each match, build the path: Connection → Database → Category Folder → Object
- Replace `RootNodes` with the filtered tree (like dependency mode does)
- Save/restore the original tree (like `_savedNodes` in dependency mode)
- Show a loading indicator while server search is in progress
- Only trigger server search after the debounce (200ms already exists) and when the filter is 2+ characters
- Fall back to the current client-side filter for already-expanded nodes (instant, no server round-trip) when all relevant nodes are loaded

### 2. Trace Tab — Connection Picker Only Shows Active Connections
**Where**: TraceView.axaml → ComboBox bound to `ConnectionNames`
**Problem**: `SetConnections()` only adds `registry.ActiveConnections`. If you navigate to the Trace tab before connecting to anything (or if you only have one connection and want to trace a different server), the dropdown is empty or has only one option.
**User experience**: "I want to trace my DEV server but I can only see PROD in the dropdown. How do I add another?"
**Fix**: Add a "Manage Connections..." option at the bottom of the dropdown, or show an empty state with guidance: "Connect to servers via File → Manage Connections first. Active connections appear here."

### 3. Execution Plan — Tied to Version History Selection, Not Query Editor
**Where**: PlanViewModel.GeneratePlanAsync() uses `_mainVm.SelectedDatabase` and `_mainVm.SelectedObject`
**Problem**: To generate an execution plan, you must:
1. Go to Version History tab
2. Select an object (proc/view/function)
3. Switch to Execution Plan tab
4. Click Generate

There's no way to generate a plan for the query you're currently writing in the editor. If nothing is selected in Version History, you get "Select an object in Version History first" — which is confusing if you thought you'd get a plan for your current query.
**User experience**: "I wrote a query, switched to Execution Plan, and it says select an object in Version History. What?"
**Fix**: Either (a) add a "Generate Plan for Current Query" flow that takes the active editor's SQL, or (b) make the empty state message much clearer: "Execution plans are generated for stored procedures from Version History. Select a proc in the Version History tab first."

### 4. Execution Plan — Uses Main Connection, Ignores Multi-Connection
**Where**: PlanViewModel gets `DatabaseService` from MainWindowViewModel
**Problem**: In multi-connection mode with tabs connected to different servers, the Execution Plan always uses the main app's initial connection — not the active tab's connection. You could be looking at a DEV tab but generating plans against PROD.
**No visual indicator** of which connection the plan is being generated against.
**Fix**: Either inherit the active query tab's connection, or clearly show which server/database the plan targets.

### 5. Version History — No Guidance When DDL Audit Table Missing
**Where**: MainWindowViewModel syncs from `VMAuditDb.dbo.DDL_Log`
**Problem**: The Version History tab depends on a DDL audit log table existing on the server. On a fresh server or one without this setup, the user gets an empty tab with no explanation.
**User experience**: "I connected successfully, but Version History is completely empty. Is it broken?"
**Fix**: Show an empty state: "No version history found. This feature requires a DDL audit trigger writing to the ObjectVersions table. See Settings → DDL Audit Source to configure."

### 6. Edit Mode — No Escape Hatch When Connection Drops
**Where**: QueryTabView edit mode
**Problem**: If you enter edit mode, type in a bunch of changes, and the connection drops — you can't Apply (no connection) and Cancel discards everything. There's no "Copy pending SQL to clipboard" to save your work.
**User experience**: "I just entered 50 rows of data, connection dropped, and my only option is Cancel which throws it all away."
**Fix**: Keep "Show SQL" working even when disconnected (it generates SQL client-side). Add "Copy SQL" button next to Show SQL so user can save their changes as a script.

### 7. Command Palette — "Run Query" and "Run with Trace" Are No-Ops
**Where**: MainWindow.BuildCommandRegistry()
**Problem**: Both entries have `Execute = () => { }`. Selecting them from the palette does literally nothing.
**User experience**: "I searched 'run' in the command palette, selected Run Query, and nothing happened."
**Fix**: Wire these to actually trigger query execution: `Execute = () => host?.RunActiveQuery()` etc. Or remove them from the palette if they can't work without the F5 key context.

---

## 🟡 Medium Priority — Confusing but Functional

### 8. Activity Monitor — Independent Connection Is Confusing
**Where**: ActivityView has its own connection button that opens a full ConnectionDialog
**Problem**: The Activity Monitor has its own connection that's separate from the main app connection. Changing it doesn't affect anything else, and changing the main connection doesn't affect Activity. The connection button looks like a label, not a clickable element.
**User experience**: "I connected to PROD but Activity Monitor is showing DEV sessions. Why?"
**Fix**: Either (a) always use the active query tab's connection and remove the separate connection, or (b) make the independent connection model very explicit with a tooltip: "Activity Monitor uses its own connection. Click to change."

### 9. Object Explorer — No Empty State When Disconnected
**Where**: QueryEditorHost OE tree
**Problem**: When all connections are disconnected (or app starts offline), the Object Explorer is just... empty. No tree, no message, nothing.
**User experience**: "Where are my databases? The sidebar is blank."
**Fix**: Show a message: "No active connections" with a "Connect..." button or link to File → Manage Connections.

### 10. Reconnect — No Manual "Go Online" Button
**Where**: MainWindow reconnect flow
**Problem**: After dismissing the reconnect overlay, the app enters offline mode with background retry every 10 seconds. But there's no way for the user to manually trigger a reconnect attempt — they have to wait for the background timer. Clicking the status bar does nothing.
**User experience**: "I fixed my VPN, but I have to wait for Lookout to notice on its own."
**Fix**: Make the "(offline)" status bar text clickable, or add a "Reconnect Now" button somewhere visible. The status bar area would be natural.

### 11. Compare Tab — Empty When Starting Offline
**Where**: CompareView connection dropdowns
**Problem**: If user chose "Continue Offline" at startup, the Compare tab's source/target dropdowns are empty with no guidance on what to do.
**User experience**: "I opened Compare but both dropdowns are empty and there's no connect button."
**Fix**: Show empty state text: "Connect to databases to compare them. Use File → Manage Connections to set up connections."

### 12. Trace Recording — No Warning on App Close
**Where**: MainWindow.OnClosing()
**Problem**: If you're actively recording a trace and close the app, the XE session on the server gets orphaned. The app cleans up orphaned sessions on next startup, but only if you connect to the same server. No warning dialog on close.
**User experience**: Silently leaves an XE session running on the server.
**Fix**: Check `TraceViewModel.State == TraceState.Recording` in OnClosing and either warn ("A trace is recording. Stop it first?") or auto-stop the trace before closing.

### 13. Tab Reconnect — Only Available via Right-Click
**Where**: QueryEditorHost.BuildTabContextMenu()
**Problem**: When a tab's connection is lost (faded dot), the only way to reconnect is right-click → Reconnect. This is discoverable only by accident. F5 does prompt for reconnect which is good, but if you just want to reconnect without running a query, you need to know about the context menu.
**User experience**: "My tab shows a faded dot. How do I reconnect? I tried clicking the dot."
**Fix**: Could make the faded dot clickable as a reconnect trigger, or show a small inline banner on the tab content: "Connection lost. [Reconnect]"

### 14. Session Restore — Silent Connection Loss
**Where**: QueryEditorHost.RestoreSession()
**Problem**: If a saved connection was deleted from the Connection Manager, tabs that used that connection silently lose their connection. No notification. The tab just works without a connection string until the user tries to run something.
**User experience**: "I opened the app and my tabs are there but nothing works. No error shown."
**Fix**: After session restore, check for orphaned tabs and show a notification: "2 tabs could not reconnect — their saved connection no longer exists."

### 15. Three-Way Compare — "T2" Button Not Self-Explanatory
**Where**: CompareView "+T2" / "-T2" toggle
**Problem**: The button text "+T2" / "-T2" is cryptic. No tooltip explains that it adds a third database for three-way comparison.
**User experience**: "What does T2 mean?"
**Fix**: Add a tooltip: "Add a second target database for three-way comparison" and consider renaming to "+ Target 2" / "- Target 2" (space permitting).

---

## 🟢 Low Priority — Polish

### 16. Query History — No Clear Option
**Where**: QueryEditorHost history panel + session service
**Problem**: Query history grows indefinitely with no way to clear it from the UI.
**Fix**: Add a "Clear History" button with confirmation in the history panel.

### 17. Intellisense — Silent Degradation
**Where**: QueryEditorHost.OnTabDatabaseChanged() — empty catch block
**Problem**: If schema loading fails, intellisense silently falls back to keywords-only. User has no idea their autocomplete is missing tables/columns.
**Fix**: Show a brief status message: "Schema loading failed — autocomplete limited to keywords" or a subtle indicator near the autocomplete toggle.

### 18. Settings — No Indication of Immediate vs Restart-Required
**Where**: SettingsDialog
**Problem**: Some settings apply immediately (font size, theme, row height), some need more context (DDL audit source, git export path). No visual distinction.
**Fix**: Group settings or add subtle labels: "Takes effect immediately" vs "Applies on next sync".

### 19. Go to Line — Dismisses on Any Click
**Where**: QueryTabView.ShowGoToLinePopup() — `box.LostFocus += CloseGoToLinePopup`
**Problem**: The popup closes on any click outside it. If you accidentally click, you have to Ctrl+G again.
**Fix**: Minor — could use Escape-only dismissal, or accept that this matches VS Code behavior.

### 20. Large Export — No Progress or Cancel
**Where**: QueryTabView.ExportResultsAsync()
**Problem**: Exporting large result sets (xlsx/csv) happens synchronously on the UI thread with no progress indicator or cancel option.
**Fix**: Wrap in a progress dialog for exports over ~10K rows.

### 21. History/Activity — Connection Buttons Look Like Labels
**Where**: MainWindow — HistoryConnectionButton, ActivityConnectionButton
**Problem**: The connection buttons on Version History and Activity Monitor look like static labels, not clickable elements. You'd only discover they're buttons by accident.
**Fix**: Add a subtle hover effect, cursor change, or small dropdown arrow to signal clickability.

---

## Summary by Priority

| Priority | Count | Key Theme |
|----------|-------|-----------|
| 🔴 High | 7 | User gets stuck, loses work, or action does nothing |
| 🟡 Medium | 8 | Confusing state with no guidance, discoverable workarounds |
| 🟢 Low | 6 | Polish, minor UX friction |

The top 3 highest-impact fixes would be:
1. **#1**: OE search returns zero results on collapsed tree — the most commonly hit issue
2. **#3 + #4**: Execution Plan connection/workflow clarity
3. **#6**: Edit mode data loss prevention on disconnect
