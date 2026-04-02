# Activity Monitor v2 — From sp_who to Real Monitoring

The Activity tab is currently a glorified sp_who2 with job CRUD bolted on. It shows what's running *right now* and lets you kill things. That's useful for emergencies but useless for the daily "is my server healthy, why is it slow, what failed overnight" workflow. This doc covers both functional gaps and visual problems.

---

## ✅ ~~Visual Overhaul — Grafana-Lite Dashboard, Not a Results Grid~~

The current Activity grid uses the same dense, left-aligned, small-font layout as the query results grid. That's wrong — the results grid is for *reading data*, the activity monitor is for *scanning at a glance*. Different purpose, different visual language.

**Design direction:** Grafana-lite. The SQL editor stays minimal, but the Activity tab gets to be visually expressive. Bordered panels with headers, colored health indicators, status pills. Not cluttered — breathing room — but with intentional use of color to convey state at a glance.

### Design Principles

1. **Theme-respecting colors only.** Reuse existing `DynamicResource` colors:
   - Green = `ButtonPrimary` (same as Run button, Save Connection)
   - Red = `ButtonDanger` (same as Don't Save, Cancel, Kill)
   - Blue = `AccentBlue` (running state, active indicators)
   - Amber/Warning = derive from existing palette or add `StatusWarning` to both themes
   - All colors via `{DynamicResource}` — never hardcoded hex in AXAML

2. **Bordered panels with headers.** Sections wrapped in `Border` with `CornerRadius`, subtle background (`PanelHeaderBackground` or slightly elevated), and a header label. Like Grafana panels but simpler.

3. **The Sessions grid is a dashboard widget**, not a query result. Taller rows, status pills, right-aligned numbers, full column headers. The Job editor panel can look like the Grafana query builder — labeled sections, bordered input groups.

### Sessions Grid Polish

- Taller rows (34px) with vertically centered text
- Font 11px — extra row height gives breathing room
- Right-align numeric columns (SPID, CPU, Blocking, Elapsed)
- Center-align status columns (Status, Command, Wait Type)
- Left-align text columns (Login, Database, Current Statement) with padding
- Full column headers: "Session ID" not "SI", "Command" not "Comm"
- Status column: colored pills — running = `AccentBlue`, suspended = amber, sleeping = gray
- Blocking column: non-zero → `ButtonDanger` background, zero → "—" dash
- Current Statement: monospace font, slightly dimmer (`TextSecondary`)
- Alternating row backgrounds (subtle, 2-3% opacity difference)

### Job Health Indicators

- Job health summary: colored indicator (green/amber/red) based on failure percentage in last 24h
  - Green (`ButtonPrimary`): 0% failed
  - Amber: 1-20% failed
  - Red (`ButtonDanger`): >20% failed
- Failed jobs get a badge count on the Jobs sub-tab header: "Jobs ● 3"
- Individual job rows: last outcome as colored pill (same color scheme)

This isn't cosmetic — it's the difference between glancing at the tab and knowing the server state in 2 seconds vs. having to squint and read every cell.

---

## ✅ ~~1. Server Health Summary Bar~~ (implemented as stat cards)

**Problem:** The view drops you straight into a grid of sessions. There's no top-level glance. You have to mentally aggregate the grid to answer "is the server okay?"

**What to add:** A summary bar between the toolbar and the grid. Single row, always visible:

```
CPU: 34%  |  Memory: 78%  |  Buffer Cache: 99.2%  |  Active: 12  |  Blocked: 2  |  TempDB: 1.2 GB used
```

**Data sources:**
- CPU: `sys.dm_os_ring_buffers` (SystemHealth) or `sys.dm_os_sys_info` + `sys.dm_os_schedulers`
- Memory: `sys.dm_os_process_memory` (physical_memory_in_use vs available)
- Buffer cache hit ratio: `sys.dm_os_performance_counters` (Buffer cache hit ratio)
- Active/Blocked: aggregate from the sessions query you already run
- TempDB: `sys.dm_db_file_space_usage` on tempdb

Color-code thresholds: green < 70%, amber 70-90%, red > 90% for CPU and Memory. Blocked > 0 = red.

This single bar makes the Activity tab useful at a glance without reading the grid at all.

---

## 2. Memory Grant Visibility

**Problem:** The most common "why is the server slow" isn't CPU or blocking — it's memory pressure. Someone's query got a giant memory grant and everything else is waiting. The current session query doesn't touch `dm_exec_memory_grants`.

**What to add:** Two things:

**A) Add memory columns to the sessions grid:**
- Requested Memory (MB) — from `dm_exec_memory_grants.requested_memory_kb`
- Granted Memory (MB) — from `dm_exec_memory_grants.granted_memory_kb`
- Grant Wait (ms) — from `dm_exec_memory_grants.wait_time_ms`

Join `sys.dm_exec_memory_grants mg ON r.session_id = mg.session_id` into the existing sessions query.

**B) In the health summary bar:** Show total granted memory and count of sessions waiting for grants. "Memory Grants: 4.2 GB granted, 2 waiting" — if any are waiting, that's a red flag.

Sessions with large grants (>256MB) should get a visual indicator in the grid — maybe a memory icon or a colored badge on the row.

---

## 3. TempDB Hogs

**Problem:** TempDB contention is the second most common "server is slow" cause, especially with warehouse operations that do big sorts and hash joins. No visibility into who's using tempdb space.

**What to add:** Add a column (hidden by default, togglable) showing per-session tempdb usage:

```sql
SELECT
    ts.session_id,
    (ts.user_objects_alloc_page_count - ts.user_objects_dealloc_page_count) * 8 / 1024 AS TempDB_User_MB,
    (ts.internal_objects_alloc_page_count - ts.internal_objects_dealloc_page_count) * 8 / 1024 AS TempDB_Internal_MB
FROM sys.dm_db_task_space_usage ts
```

Sessions using >100MB of tempdb should be flagged. In the health bar, show total tempdb usage.

---

## ✅ ~~4. Blocking Chain Visualization — Fix the Logic~~

**Problem:** The current `UpdateBlockingChains` method is incomplete. It finds directly blocked sessions but doesn't walk transitive chains (A blocks B blocks C shows as two separate pairs instead of one chain). For warehouse operations where one long cycle count blocks 15 pick operations, the current display is misleading.

**What to fix:**
- Walk the full chain from head blocker to all leaves
- Show depth: "Session 55 → Session 72 → Session 88 (depth 3)"
- Show the head blocker's query text in the banner (that's the one you need to decide about killing)
- Make SPIDs in the chain clickable — clicking scrolls to and selects that session in the grid
- If there are multiple independent chains, show them on separate lines, not pipe-separated on one line

---

## ✅ ~~5. Failed Jobs Alert Badge~~

**Problem:** You have to actively navigate to the Jobs tab and apply the "Failed only" filter to discover failures. If a job failed at 3 AM, you won't know until you go looking.

**What to add:**
- A badge on the Activity tab header in the top bar: "Activity ⚠ 3" when there are jobs that failed in the last 24 hours
- A banner at the top of the Jobs sub-tab (similar to the blocking chain banner for Sessions): "3 jobs failed in the last 24 hours" in red/amber, with job names listed
- This data comes from the existing `GetJobsDashboardAsync` query — just filter for `LastRunOutcome == "Failed"` and `LastRunDate > DATEADD(HOUR, -24, GETDATE())`
- Clicking the banner applies the "Failed only" filter automatically

---

## 6. Step-Level Error Details for Failed Jobs

**Problem:** The History tab shows job-level outcome rows (`step_id = 0` from `sysjobhistory`). The message is usually useless: "The job failed. The Job was invoked by Schedule 'Every 5 min'." The actual error — the T-SQL error message from the step that failed — is in a different history row (`step_id > 0`).

**What to fix:** When showing history for a failed run, also fetch the step-level history rows for that `instance_id` range. Show the actual failing step name and its error message:

```
Step 3 "Update Inventory" failed: Msg 547, Level 16 — The UPDATE statement conflicted with the FOREIGN KEY constraint...
```

This is the difference between "something failed" and "I know exactly what failed and why" without leaving the Activity tab.

---

## 7. Wait Stats Summary (Server-Wide)

**Problem:** Per-session wait types are shown, but there's no server-wide view. When the server is generally slow but no single session looks bad, you need `sys.dm_os_wait_stats` to see the aggregate pattern (is it PAGEIOLATCH_SH everywhere? CXPACKET? LCK_M_X?).

**What to add:** A collapsible section or a third sub-tab: "Waits". Shows the top 10 wait types by cumulative wait time, excluding benign waits (WAITFOR, BROKER_RECEIVE_WAITFOR, LAZYWRITER_SLEEP, etc.). Auto-refreshes alongside sessions.

This is lower priority than #1-#6 but it's the kind of thing that turns the Activity tab from "emergency tool" into "daily diagnostic tool."

---

## 8. No Historical Context

**Problem:** Auto-refresh shows you *right now*. When someone says "the server was slow at 3 AM," you have nothing. No session snapshots, no blocking history.

**What to add (lightweight approach):** Keep the last N refresh snapshots in memory (say, last 30 minutes at whatever the refresh interval is). Add a small timeline scrubber below the grid. Dragging it backwards shows the historical snapshot. "2 min ago: 15 active, 4 blocked" → you can see that blocking event that resolved itself.

This doesn't need database persistence — just in-memory ring buffer of the session list snapshots. It's lost on app restart, which is fine. The value is "I was on a call for 10 minutes and missed what happened."

This is the most ambitious item on the list — do it last, if at all.

---

## Implementation Priority

1. **Visual overhaul** — taller rows, centered text, right-aligned numbers, status pills, full column headers (pure UI, no backend changes)
2. **Server health summary bar** — biggest bang for effort, makes the tab useful at a glance
3. **Failed jobs alert badge** — zero backend work, just filter existing data
4. **Step-level error details** — small query change, big diagnostic value
5. **Blocking chain fix** — walk transitive chains properly, make clickable
6. **Memory grant columns** — join one more DMV into sessions query
7. **TempDB per-session usage** — same approach as memory grants
8. **Wait stats summary** — new sub-tab or collapsible section
9. **Historical snapshots** — in-memory ring buffer with timeline scrubber
