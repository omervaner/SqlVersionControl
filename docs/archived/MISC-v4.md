# MISC v4 — UX Wins from SSMS Pain Points

Ideas sourced from analyzing the most common complaints against SSMS, Azure Data Studio, and conventional SQL tools — then asking what Lookout can do better.

---

## ✅ ~~1. One-Click Copy Result Set~~

**The SSMS pain:** Copying query results is a multi-step ritual. Ctrl+A → Ctrl+Shift+C gets you headers + data but most people don't know the shortcut. Right-click → Select All → right-click again → Copy with Headers is what they actually do. And the clipboard format includes trailing newlines and sometimes BOM characters that break pastes into chat tools.

**What we do:** Add a small copy icon button (📋) on the result tab header bar, next to the existing pin button. Behavior:

- **No rows selected:** copies the entire result set (headers + all rows) as clean TSV
- **Rows selected:** copies only selected rows with headers as TSV
- Keyboard shortcut: `Cmd/Ctrl+Shift+C` (matches SSMS's "Copy with Headers" muscle memory)
- Brief status flash: "✓ 247 rows copied" (uses existing QueryFlash infrastructure)

TSV format, not CSV — tabs paste into Excel cell-by-cell automatically without the import wizard. Anyone pasting into Slack/Teams doesn't care either way.

**Where it goes:** `QueryTabView.Results.cs` for the single-result grid, plus wire it into stacked mode grids. The button sits in the result tab header panel (the bar that has "Result (247 rows)" + pin button).

**Also add to stacked mode:** Each stacked result's header bar gets its own copy button (copies just that result set).

---

## 2. Result Set Diff — Compare This Run vs Last Run — PARKED (needs design doc)

**The SSMS pain:** You run a query, tweak an index, run it again. Now you're trying to eyeball-diff two result grids to see if the row counts or values changed. People screenshot the first result or paste it into Excel to compare manually.

**What we do:** Leverage the existing pinned results feature. When a result is pinned and a new query runs, add a "Compare with pinned" option in the result tab context menu. Opens a side-by-side or inline diff showing:

- Row count delta
- New rows (in pinned but not in current, and vice versa)
- Changed values (same PK, different column values)

This requires a PK or unique key to match rows — if none exists, fall back to row-order comparison with a warning. Uses the existing DiffPlex infrastructure.

Lower priority than #1 — this is a "wow" feature, not a daily need. But it's something no mainstream SQL IDE does well.

---

## ✅ ~~3. Smart Reconnect After Sleep — Silent + Visual~~

**The SSMS pain:** Laptop sleeps, wakes up. SSMS doesn't know the connection died. Your next query hangs for 30 seconds before timing out, then you get a cryptic "Transport-level error" or "Connection was broken." You have to manually reconnect or open a new query window.

**What we already do:** `SleepDetector` fires `WokeFromSleep` after detecting a >2min gap. But what happens after that event fires? Verify the full flow:

- Does it proactively test all active connections in the registry?
- Does it update the connection dots on tabs that lost their connection?
- Does it attempt silent reconnect before the user's next query?

The ideal UX: wake from sleep → Lookout silently tests all connections in background → if any dropped, it reconnects using stored credentials → if reconnect fails (VPN not up yet), the tab dot goes faded and the reconnect ↻ button appears — all before the user types anything. Zero friction in the happy path.

**Check:** Does the current `WokeFromSleep` handler do all of this, or does it just log? If it's partial, flesh it out.

---

## ✅ ~~4. Query Elapsed Time in Tab Title While Running~~

**The SSMS pain:** You have 8 query tabs open. One is running a long query. Which one? You have to click through tabs to find the one with the spinner. There's no visual indicator on the tab itself.

**What we do:** While a query is running, update the tab title to show elapsed time: `"My Query ⟳ 12s"`. When it finishes, revert to the normal title. The `QueryStatusText` already tracks elapsed time via `StartElapsedTimer()` — just propagate it to `TabTitle` during execution.

Subtle but useful when you have many tabs open and are waiting on one long query while working in another.

---
