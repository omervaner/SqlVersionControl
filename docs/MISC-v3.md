# MISC v3 — Compare Tab & Other Fixes

---

## 1. Compare Tab — Multiple UX Issues

The Compare tab has several related problems that together make it confusing and untrustworthy. These should be fixed as a batch.

### 1A. Source Shows "Not Connected" While Actively Showing Data ✅ DONE
**What happens**: The Source ConnectionIndicator shows "Not connected" with a grey dot, but the comparison grid is full of data with "Source Only" columns. The status bar at the bottom shows PROD. The connection clearly exists — the indicator just doesn't know about it.
**Root cause**: The old Compare flow auto-connects Source silently after Target connects (the `ConnectTargetAsync()` pattern from Claude.MD Issue #11-13). The new ConnectionIndicator wasn't wired into this silent auto-connect path. When Source connects in the background, nobody calls the indicator's update method.
**Fix**: When Source auto-connects (or connects by any path), update the Source ConnectionIndicator state to reflect the active connection. The indicator must stay in sync with the actual connection state at all times.

### 1B. Shows Comparison Data Before Both Sides Are Selected ✅ DONE
**What happens**: Before the user has selected a Target, the Compare tab already shows data — it appears to be comparing Source against itself, or showing Source-only data as if a comparison happened. This is misleading because the user hasn't asked for a comparison yet.
**Expected behavior**: The comparison grid should be empty until BOTH a Source and a Target are connected and a scan has been triggered. Before that, show an overlay.
**Fix**: Add a text overlay on the comparison area:
- **Neither connected**: "Select a source and target database to compare"
- **Only Source connected**: "Select a target database to compare against"
- **Only Target connected**: "Select a source database to compare from"
- **Both connected, no scan yet**: "Click Refresh to scan for differences"

The overlay disappears once a scan completes and results are displayed.

### 1C. No Loading Indicator When Switching Targets — Stale Data Persists ✅ DONE
**What happens**: User is comparing PROD vs QA and examining table structure differences. They switch Target from QA to DEV. The old QA comparison data stays on screen for 5-10 seconds while the new DEV scan runs in the background. Then suddenly the grid updates with DEV data. During those 5-10 seconds, the user is looking at stale QA data but the Target indicator already says DEV — the screen lies.
**Expected behavior**: The moment a Target (or Source) connection changes, the old comparison data should be cleared immediately and a loading indicator should appear: "Scanning differences..." or a spinner. The grid repopulates only when the new scan completes.
**Fix**:
1. On connection change (Source or Target), immediately clear the comparison results grid
2. Show a loading overlay: "Scanning [Source] vs [Target]..." with a spinner or progress indicator
3. Auto-trigger a new scan when the connection changes (don't wait for manual Refresh click)
4. If the scan is cancelled (user switches again mid-scan), clear and restart — use a CancellationTokenSource that gets replaced on each new scan

This is the most important of the three because it causes the user to look at wrong data and potentially make wrong decisions (e.g. deploying based on stale comparison).

---

### 1D. No "Swap Source ↔ Target" Button ✅ DONE
**What happens**: You accidentally set PROD as Target and DEV as Source. Now you'd have to manually change both dropdowns. Every database comparison tool (SSMS Schema Compare, Redgate, dbForge) has a swap button.
**Fix**: Add a "⇄" swap button between the Source and Target ConnectionIndicators. One click swaps both connections and re-triggers the scan.

### 1E. Deploy Direction Is Implicit ✅ DONE
**What happens**: The deploy buttons say "Deploy to Target 1" but don't specify *from where*. The arrows on individual rows (→) help, but the bottom bar button doesn't make it explicit. If you've been staring at the screen for a while and lose track of which side is which, you could deploy the wrong direction.
**Fix**: Keep the button text as-is ("Deploy to Target 1") but add a dynamic tooltip that shows the actual connection names: "Deploy from PROD TestDB → DEV TestDB". The tooltip updates whenever the Source or Target connection changes. This gives full clarity on hover without cluttering the button.

### 1F. Data Compare on Large Tables — No Row Limit Warning ✅ DONE
**What happens**: User clicks Data mode and selects a table with 5 million rows. Does it try to load all rows into memory for comparison? There's a Refresh button but no visible row limit.
**Fix**: Either set a sensible default limit (e.g. TOP 10000 with a message "Showing first 10,000 rows — add WHERE clause for targeted comparison") or prompt before loading: "This table has ~5M rows. Compare first 10,000?"

---

## 2. Editor — Rotating Tips in Placeholder Text ✅ DONE

---

## 3. App Icon — Use Simplified Eye Design ✅ DONE


---

## 4. In-App Error Log Viewer ✅ DONE

Now that all silent catch blocks are logged via `AppLogger`, users should be able to see these logs without finding the file on disk. Two levels:

### 4A. Help → View Log File (quick win)
Add a menu item under Help that reads `app.log` and opens it in a new query tab as read-only text. Users at Gratis can copy-paste relevant sections when reporting issues. Ten lines of code.

### 4B. Status Bar Error Counter (better version)
A small indicator in the status bar that tracks errors logged during the current session. Hidden or shows "0" when clean, turns red with a count when `AppLogger.LogError` fires. Click to open a filtered view of just this session's errors.

Implementation: Add an in-memory `List<QueryMessage>` alongside the file log in `AppLogger`. Each `LogError` call appends to both. The status bar binds to the count. Clicking opens a simple dialog or panel showing the list.

**Important**: Any new dialogs or panels must use `{DynamicResource}` theme bindings — no hardcoded colors. Follow the existing pattern in AppTheme.axaml / AppThemeLight.axaml. Both dark and light themes must look correct.


---

## 5. Status Bar — Active Connections Tooltip ✅ DONE
**Where**: MainWindow status bar — the connection area (colored dot + connection name)
**Problem**: The status bar only shows the primary/main editor connection. But the app can have 5+ active connections at once: multiple editor tabs on different servers, Activity on another, Compare with Source/Target, Trace on yet another. There's no single place to see all active connections at a glance.
**Fix**: On hover over the status bar connection area, show a rich tooltip listing all active connections:
```
Active Connections (5):
● PROD TestDB (localhost,1433) — Editor: Query 1, 2, 3
● DEV TestDB (localhost,1434) — Editor: Query 4 · Compare Target
● QA TestDB (10.0.1.15) — Activity Monitor
● PROD TestDB — Compare Source
● STG TestDB — Trace
```
Each line shows: colored environment dot, connection name, server address, and which tabs/features are using it. No click needed — just hover and glance. This is a read-only tooltip, not a dialog.

Implementation: Build the tooltip content dynamically from `ConnectionRegistry.ActiveConnections` plus per-tab connection info from `QueryEditorHost`. Use a styled `ToolTip` with a monospace font for alignment. Must respect theming — use `{DynamicResource}` for all colors.


---

## 6. Stacked Results (SSMS Style) + Pin Tab

### The Problem

Currently, when a query returns multiple result sets (e.g. two SELECTs separated by GO, or a proc that returns multiple result sets), each one gets its own tab in the result tab strip: `[Result 1 (150 rows)] [Result 2 (42 rows)] [Result 3 (10 rows)] [Messages]`. This means a lot of tab-clicking to review results. SSMS stacks all results vertically in a single scrollable area — you see everything at once.

### New Default Behavior — Stacked Results

All result sets from a single execution render in one scrollable panel, stacked vertically. Each result set has a small header bar above its grid:

```
┌─────────────────────────────────────────────────────┐
│ Result 1 — 150 rows                          [Pin]  │
├─────────────────────────────────────────────────────┤
│  EmployeeId  │  FirstName  │  LastName  │  Salary   │
│  1           │  Sarah      │  Chen      │  185000   │
│  2           │  James      │  Wilson    │  145000   │
│  ...         │             │            │           │
├─────────────────────────────────────────────────────┤
│ Result 2 — 42 rows                           [Pin]  │
├─────────────────────────────────────────────────────┤
│  DepartmentId │  Department  │  Budget    │          │
│  1            │  Engineering │  2500000   │          │
│  ...          │              │            │          │
├─────────────────────────────────────────────────────┤
│ Result 3 — 10 rows                           [Pin]  │
├─────────────────────────────────────────────────────┤
│  ...                                                │
└─────────────────────────────────────────────────────┘
```

The whole area is a single ScrollViewer. Each result set is a DataGrid with a fixed header bar above it showing the result label, row count, and a [Pin] button.

### Result Tab Strip — Simplified

The tab strip changes from many tabs to just a few:

**Before (current):**
```
[Result 1 (150 rows)] [Result 2 (42 rows)] [Result 3 (10 rows)] [Messages]
```

**After:**
```
[Results (3 sets, 202 rows)] [Messages] [Pinned: Result 1]
```

- **"Results" tab**: Shows all stacked grids. The label shows total set count and total row count.
- **"Messages" tab**: Unchanged — shows messages, errors, timing.
- **"Pinned: ..." tabs**: One tab per pinned result. Only appears when user explicitly pins a result.

For single-result queries (the most common case), the tab strip looks identical to now: `[Result (150 rows)] [Messages]`. The stacking only matters when there are multiple result sets.

### Pin Tab Behavior

Each result set header has a small [Pin] button (📌 or a thumbtack icon). Clicking it:

1. Creates a new tab in the result tab strip: "Pinned: Result 1" (or a custom label if we want to get fancy later)
2. Copies the result data into the pinned tab — it now has its own independent DataGrid
3. **Pinned results survive the next F5.** When the user runs a new query, the stacked results are replaced but pinned tabs remain. This is the core value of pinning — "I want to keep this result for comparison."
4. Pinned tabs have an × close button like query tabs
5. Pinned tabs show a subtle visual distinction (different tab background, pin icon, or border) so they're clearly different from the live Results tab

### What Happens on F5 (Next Execution)

1. The "Results" tab content is completely replaced with new stacked results
2. The "Messages" tab content is completely replaced with new messages
3. All "Pinned: ..." tabs are **preserved** — they're snapshots, unaffected by new executions
4. The active tab switches to "Results" (or "Messages" if there were errors, following SSMS behavior)

### Implementation Notes

**Stacked results container**: Replace the current single `ResultsGrid` DataGrid with a `ScrollViewer` containing an `ItemsControl` of result panels. Each panel has a header Border + DataGrid. The ItemsControl binds to the `Results` collection on the ViewModel.

**Result panel header**: A Border with `PanelHeaderBackground`, showing:
- Left: "Result {N} — {RowCount} rows" label
- Right: [Pin] button (small, secondary style)

**DataGrid per result**: Each grid is independently sortable and resizable. Column widths are per-grid (not synced across stacked results). Each grid should have a reasonable max height (e.g., 400px) before it scrolls internally, so one massive result set doesn't push the others off screen. If only one result set exists, the max height constraint doesn't apply — it fills the available space like it does now.

**Pin data model**: Add to QueryTabViewModel:
```csharp
[ObservableProperty]
private ObservableCollection<PinnedResult> _pinnedResults = new();

public class PinnedResult
{
    public string Label { get; set; } = "";
    public DataTable Data { get; set; }
    public List<string> ColumnNames { get; set; } = [];
    public int RowCount { get; set; }
    public DateTime PinnedAt { get; set; }
}
```

**Tab strip wiring**: `RebuildResultTabs()` now builds:
1. One "Results" tab (always, if there are any results)
2. One "Messages" tab (always)
3. One tab per entry in `PinnedResults` collection
4. The Exec Plan tab (when available, from the Ctrl+L flow)

**Cell detail panel**: Still works — clicking a cell in any stacked grid (or pinned grid) shows the cell detail strip at the bottom. The detail strip binds to whichever DataGrid last had a cell selected.

**Export button**: When on the stacked "Results" tab, Export should either export all result sets (as separate sheets in xlsx, or concatenated in csv with headers), or show a small picker: "Export Result 1 / Result 2 / All". For pinned tabs, Export exports just that pinned result.

**Edit mode**: Edit mode should only be available on single-result queries (where the stacked view is effectively identical to the current view). For multi-result queries, the Edit button is hidden — this matches SSMS behavior where editing is only possible on single-result grids.

### Migration Path

This is a significant change to `QueryTabView` results rendering. The safest approach:

1. Build the stacked results `ScrollViewer` + `ItemsControl` + per-result `DataGrid` layout
2. Wire it to the existing `Results` collection — the data model doesn't change, just the rendering
3. Add the [Pin] button and `PinnedResults` collection
4. Update `RebuildResultTabs()` to generate the simplified tab strip
5. Test: single result, multi result, pin, F5 after pin, export, cell detail, edit mode

**Must respect theming** — all new panels, headers, and pin buttons use `{DynamicResource}` bindings. Test in both dark and light themes.


### Export Behavior for Stacked Results

The Export button in the result tab bar needs to handle multi-result queries. Three paths:

**Export All (xlsx — primary path):**
When user clicks Export while on the stacked "Results" tab with multiple result sets, default to xlsx with each result set as a separate sheet. Sheet names: "Result 1", "Result 2", etc. Each sheet has its own column headers, independent formatting. One click, one file, all results cleanly separated. This is the high-value use case — run a diagnostic query that returns order headers, line items, pick info as 3 SELECTs, export to xlsx, send to the team with 3 clean tabs.

**Export Single:**
Each result set header in the stacked view has its own small [Export] button (or right-click → Export). Exports just that one result set as csv or xlsx (single sheet). Same behavior as the current single-result export.

**Export All (csv fallback):**
CSV has no sheets concept. For multi-result CSV export, concatenate results with a blank line separator and a header comment between them:
```
-- Result 1 (150 rows)
EmployeeId,FirstName,LastName,Salary
1,Sarah,Chen,185000
2,James,Wilson,145000

-- Result 2 (42 rows)
DepartmentId,Department,Budget
1,Engineering,2500000
```

**Pinned results:** Export on a pinned tab exports just that pinned result. No ambiguity.

**Export dialog:** When on stacked results with multiple sets, the existing export dialog (or a small flyout) should offer: "Export All Results (xlsx)" / "Export All Results (csv)" / Cancel. For single-result queries, behave exactly like today — no extra dialog.
