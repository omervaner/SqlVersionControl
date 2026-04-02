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

### 1D. No "Swap Source ↔ Target" Button
**What happens**: You accidentally set PROD as Target and DEV as Source. Now you'd have to manually change both dropdowns. Every database comparison tool (SSMS Schema Compare, Redgate, dbForge) has a swap button.
**Fix**: Add a "⇄" swap button between the Source and Target ConnectionIndicators. One click swaps both connections and re-triggers the scan.

### 1E. Deploy Direction Is Implicit
**What happens**: The deploy buttons say "Deploy to Target 1" but don't specify *from where*. The arrows on individual rows (→) help, but the bottom bar button doesn't make it explicit. If you've been staring at the screen for a while and lose track of which side is which, you could deploy the wrong direction.
**Fix**: Keep the button text as-is ("Deploy to Target 1") but add a dynamic tooltip that shows the actual connection names: "Deploy from PROD TestDB → DEV TestDB". The tooltip updates whenever the Source or Target connection changes. This gives full clarity on hover without cluttering the button.

### 1F. Data Compare on Large Tables — No Row Limit Warning
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

## 5. Status Bar — Active Connections Tooltip
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
