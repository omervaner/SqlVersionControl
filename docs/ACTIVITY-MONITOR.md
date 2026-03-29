# Activity Monitor — Feature Spec

A new view tab (alongside Editor, History, Compare, Exec Plan) for real-time server monitoring. The sp_who/sp_who2 replacement — but better.

---

## 1. View Tab Placement

New tab in the top bar: `Editor  History  Compare  Exec Plan  **Activity**  Settings`

Owns its own connection (same as other views — per-view connection architecture from QUALITY-POLISH.md Section 2). Has its own connection selector if needed.

---

## 2. Sub-Tabs

Two sub-tabs within the Activity view:

### Tab A: Active Sessions (sp_who replacement)

**What:** Real-time view of all sessions/queries running on the server.

**Query:** Combine `sys.dm_exec_sessions`, `sys.dm_exec_requests`, and `sys.dm_exec_sql_text`:

```sql
SELECT
    s.session_id AS [Session ID],
    s.login_name AS [Login],
    DB_NAME(s.database_id) AS [Database],
    s.status AS [Session Status],
    r.status AS [Request Status],
    r.command AS [Command],
    r.wait_type AS [Wait Type],
    r.wait_time AS [Wait Time (ms)],
    r.blocking_session_id AS [Blocking Session],
    r.cpu_time AS [CPU (ms)],
    r.reads AS [Reads],
    r.writes AS [Writes],
    r.logical_reads AS [Logical Reads],
    DATEDIFF(SECOND, r.start_time, GETDATE()) AS [Elapsed (s)],
    r.percent_complete AS [% Complete],
    r.open_transaction_count AS [Open Trans],
    s.host_name AS [Host],
    s.program_name AS [Program],
    t.text AS [Query Text],
    SUBSTRING(t.text, (r.statement_start_offset/2)+1,
        ((CASE r.statement_end_offset
            WHEN -1 THEN DATALENGTH(t.text)
            ELSE r.statement_end_offset
        END - r.statement_start_offset)/2)+1) AS [Current Statement]
FROM sys.dm_exec_sessions s
LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE s.is_user_process = 1
ORDER BY r.cpu_time DESC
```

**Grid columns (default visible):**
- Session ID
- Login
- Database
- Request Status (running/suspended/sleeping/killed/rollback)
- Command (SELECT/INSERT/UPDATE/DELETE/BACKUP etc.)
- Wait Type
- Blocking Session (highlighted red if non-zero)
- CPU (ms)
- Elapsed (s)
- Current Statement (truncated, full text in tooltip or detail panel)

**Grid columns (available but hidden by default):**
- Host, Program, Reads, Writes, Logical Reads, Wait Time, Open Trans, % Complete, Full Query Text

**Filters at the top:**
- Checkbox: "Hide sleeping sessions" (on by default — show only active queries)
- Checkbox: "Hide system sessions" (on by default — already filtered by `is_user_process = 1`)
- Database filter dropdown (optional — filter to specific database)
- Text search box (filter by login, host, query text)

**Auto-refresh:**
- Toggle button: "Auto-refresh" with interval dropdown (1s, 2s, 5s, 10s, 30s). Default: 5s, off by default.
- Manual "Refresh" button always available.

**Actions:**
- **Kill Session:** Select a row → "Kill" button (or right-click → Kill Session). Shows a confirmation dialog: "Kill session {id}? Login: {login}, Query: {truncated_query}". Executes `KILL @session_id`.
- **Kill with rollback status:** For sessions already in KILLED/ROLLBACK state, show the `percent_complete` so you can see rollback progress. Option to re-issue KILL if it's stuck.
- **Copy Query:** Right-click → "Copy Query Text" to clipboard.
- **Open in Editor:** Right-click → "Open Query in Editor" — opens the full query text in a new editor tab.

**Blocking visualization:**
- Rows that are blocking other sessions get a colored indicator (orange/red).
- The "Blocking Session" column is clickable — clicking it selects/scrolls to the blocking session.
- Optional: a simple blocking chain text view at the top: "Session 55 → blocks → Session 72 → blocks → Session 88"

---

### Tab B: Jobs Dashboard (upgraded from OE)


**What:** Full Job Activity Monitor — SSMS-level detail with extras. Real-time view of all SQL Agent jobs on the server.

**Reuse:** Existing `GetJobsAsync`, `GetJobStepsAsync`, `GetJobHistoryAsync`, `StartJobAsync` from DatabaseService. Extend with schedule info, categories, and enable/disable.

**Main query (new, replaces simple GetJobsAsync for this view):**
```sql
SELECT
    j.name AS JobName,
    j.enabled AS IsEnabled,
    cat.name AS Category,
    CASE
        WHEN ja.run_requested_date IS NOT NULL AND ja.stop_execution_date IS NULL THEN 'Executing'
        WHEN ja.run_requested_date IS NOT NULL AND ja.stop_execution_date IS NOT NULL THEN 'Idle'
        ELSE 'Idle'
    END AS CurrentStatus,
    CASE ISNULL(h.run_status, -1)
        WHEN 0 THEN 'Failed'
        WHEN 1 THEN 'Succeeded'
        WHEN 2 THEN 'Retry'
        WHEN 3 THEN 'Cancelled'
        WHEN 4 THEN 'In Progress'
        ELSE 'Unknown'
    END AS LastRunOutcome,
    CASE WHEN h.run_date IS NOT NULL
         THEN msdb.dbo.agent_datetime(h.run_date, h.run_time)
         ELSE NULL
    END AS LastRunDate,
    CASE WHEN h.run_duration IS NOT NULL
         THEN (h.run_duration / 10000) * 3600 + ((h.run_duration / 100) % 100) * 60 + (h.run_duration % 100)
         ELSE NULL
    END AS LastDurationSec,
    ja.next_scheduled_run_date AS NextRunDate,
    CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps js2 WHERE js2.job_id = j.job_id)
         THEN 1 ELSE 0
    END AS IsRunnable,
    CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobschedules js3 WHERE js3.job_id = j.job_id)
         THEN 1 ELSE 0
    END AS IsScheduled,
    sched.name AS ScheduleName,
    sched.freq_type,
    sched.freq_interval,
    sched.freq_subday_type,
    sched.freq_subday_interval,
    sched.active_start_time,
    j.description AS JobDescription,
    (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps js4 WHERE js4.job_id = j.job_id) AS StepCount
FROM msdb.dbo.sysjobs j
LEFT JOIN msdb.dbo.syscategories cat ON j.category_id = cat.category_id
LEFT JOIN msdb.dbo.sysjobactivity ja
    ON ja.job_id = j.job_id
    AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
LEFT JOIN msdb.dbo.sysjobhistory h
    ON h.job_id = j.job_id AND h.step_id = 0
    AND h.instance_id = (SELECT MAX(h2.instance_id) FROM msdb.dbo.sysjobhistory h2 WHERE h2.job_id = j.job_id AND h2.step_id = 0)
LEFT JOIN msdb.dbo.sysjobschedules js ON j.job_id = js.job_id
LEFT JOIN msdb.dbo.sysschedules sched ON js.schedule_id = sched.schedule_id
ORDER BY j.name
```

**Grid columns (default visible):**
- Job Name
- Enabled (Yes/No — green dot for enabled, gray for disabled)
- Status (Executing/Idle — "Executing" highlighted with animated or pulsing indicator)
- Last Run Outcome (Succeeded/Failed/Cancelled/Unknown — color coded: green/red/yellow/gray)
- Last Run (date/time)
- Last Duration (human readable: "1m 23s", "45s", "2h 5m")
- Next Run (date/time — blank if no schedule)
- Category
- Schedule (human-readable: "Every 5 min", "Daily at 02:00", "Mon/Wed/Fri at 08:00", etc.)

**Grid columns (available but hidden by default):**
- Runnable (Yes/No — has steps)
- Scheduled (Yes/No — has a schedule assigned)
- Description
- Step Count

**Color coding rules:**
- Last Run Outcome: Succeeded = green background/text, Failed = red, Cancelled = yellow, Unknown = gray
- Enabled: green dot = enabled, gray dot = disabled
- Status: "Executing" = blue/animated indicator, "Idle" = no indicator
- Next Run in the past (missed run) = orange/warning

**Human-readable schedule formatting:**
Convert `freq_type`, `freq_interval`, `freq_subday_type`, `freq_subday_interval`, `active_start_time` into readable strings:
- `freq_type = 1` → "Once"
- `freq_type = 4` → "Every {freq_interval} day(s) at {time}"
- `freq_type = 4, freq_subday_type = 4` → "Every {freq_subday_interval} min"
- `freq_type = 4, freq_subday_type = 8` → "Every {freq_subday_interval} hour(s)"
- `freq_type = 8` → decode day bitmask (1=Sun, 2=Mon, 4=Tue, 8=Wed, 16=Thu, 32=Fri, 64=Sat) → "Mon/Wed/Fri at {time}"
- `freq_type = 16` → "Day {freq_interval} of every month at {time}"
- `freq_type = 32` → monthly relative (first Monday, etc.)
- `freq_type = 64` → "When SQL Agent starts"
- `freq_type = 128` → "When CPU is idle"

Build a `FormatJobSchedule()` helper method for this — it's reusable.

**Filters at the top:**
- Text search box (filter by job name)
- Dropdown: "All" / "Enabled only" / "Disabled only"
- Dropdown: "All outcomes" / "Failed only" / "Succeeded only"
- Category dropdown (populated from distinct categories in the data)
- Checkbox: "Show only running" (default: off)

**Actions (buttons above the grid + right-click context menu):**
- **Start Job:** Select row → "Start" button. Confirmation dialog: "Start job '{name}'?". Reuses existing `StartJobAsync`.
- **Stop Job:** For running jobs → "Stop" button. Executes `msdb.dbo.sp_stop_job @job_name`. Confirmation required.
- **Enable / Disable Job:** Toggle button. Executes `msdb.dbo.sp_update_job @job_name, @enabled = 0/1`. Confirmation required for disable.
- **View Steps:** Select row → "Steps" panel expands below the grid showing all steps in a secondary DataGrid (Step #, Name, Type/Subsystem, Command preview). Double-click a step to see the full command text. Reuses `GetJobStepsAsync`.
- **View History:** Select row → "History" panel expands below the grid showing last 20 runs in a secondary DataGrid (Date, Outcome, Duration, Message). Color-coded rows (green/red). Reuses `GetJobHistoryAsync` (bump from 10 to 20 rows).
- **Open Step in Editor:** Right-click a step → opens its TSQL command in a new query tab.
- **Refresh:** Manual refresh button.

**Detail panel (below the grid):**


**Detail panel (below the grid) — Job Properties Editor:**
When a job is selected, a detail panel expands below the grid. This is a full inline property editor — no popups. Four tabs:

**General tab:**
- Job Name (editable text field)
- Description (editable text area)
- Enabled toggle (checkbox)
- Category dropdown (populated from `msdb.dbo.syscategories`)
- "Save" button → executes `sp_update_job` with the changed fields

**Steps tab:**
- DataGrid with Step ID, Step Name, Subsystem (TSQL/CmdExec/SSIS/PowerShell), On Success action, On Failure action, Command preview
- Click a step → command text loads in an editable text area below the steps grid (mini editor, not full AvaloniaEdit — just a TextBox with monospace font)
- "Save Step" button → `sp_update_jobstep` with changed command/name
- "Add Step" button → adds a new row, user fills in name, type, command → `sp_add_jobstep`
- "Delete Step" button → confirmation, then `sp_delete_jobstep`
- Drag to reorder (or move up/down buttons if drag is too complex in Avalonia DataGrid)
- Right-click step → "Open in Editor" → opens step command in a new query tab

**Schedule tab:**
- If the job has a schedule, show the current settings pre-filled. If no schedule, show "No schedule — click Add to create one."
- Frequency type dropdown: Once, Daily, Weekly, Monthly, When SQL Agent starts, When CPU idle
- For Daily: "Every N day(s)" spinner
- For Weekly: day-of-week checkboxes (Mon/Tue/Wed/Thu/Fri/Sat/Sun) + "Every N week(s)" spinner
- For Monthly: day-of-month spinner + "Every N month(s)" spinner
- Subday frequency: "Occurs once at [time picker]" OR "Occurs every [N] [minutes/hours] between [start time] and [end time]"
- Start time picker (hour:minute)
- "Save Schedule" button → `sp_update_jobschedule` for existing, `sp_add_jobschedule` for new
- "Remove Schedule" button → confirmation, then `sp_delete_jobschedule`

**History tab:**
- DataGrid with Run Date, Outcome (color coded green/red), Duration, Message
- Last 20 runs. Reuses `GetJobHistoryAsync` (bumped from 10 to 20 rows)
- Read-only — no editing here

**Panel behavior:**
- Clicking a job expands the panel (if collapsed) or updates it with the new job's data
- Clicking the same job again collapses the panel
- Escape collapses the panel
- A subtle "unsaved changes" indicator if the user edits something without saving
- The GridSplitter between the jobs grid and detail panel should resize properly — jobs grid row is `*`, splitter row is `Auto`, detail panel row is `*` with a min-height


**Auto-refresh:** Same toggle as Tab A — shared interval setting.


## 3. Kill Session — Safety

Killing a session is serious, especially on PROD. Safety measures:

- **Always requires confirmation dialog.** No exceptions. Dialog shows: session ID, login name, database, elapsed time, and the first 200 chars of the query.
- **Color-coded by risk:** If the session is on a production database (if identifiable from connection name), the confirmation dialog background is red/warning colored.
- **Cannot kill your own session.** Gray out the Kill button for the current app's session ID (get via `SELECT @@SPID`).
- **Log kills:** When a session is killed, log it to the Messages area or status bar: "Killed session {id} ({login}) at {timestamp}."

---

## 4. Implementation Notes


**Reuse from existing code:**
- `GetJobsAsync`, `GetJobStepsAsync`, `GetJobHistoryAsync`, `StartJobAsync` — already in DatabaseService
- `ToMsdbConnection()` helper — already exists for msdb queries
- Dialog base styling — already standardized (TOOLS-MENU.md Section 14)
- Per-view connection pattern — already established (QUALITY-POLISH.md Section 2)

**New in DatabaseService:**
- `GetActiveSessionsAsync(connectionString)` — the DMV query from Tab A
- `KillSessionAsync(connectionString, sessionId)` — executes `KILL @id`
- `GetCurrentSpidAsync(connectionString)` — `SELECT @@SPID` to prevent self-kill
- `GetJobsDashboardAsync(connectionString)` — the expanded job query with categories, schedules, status, step counts (replaces simple `GetJobsAsync` for this view)
- `StopJobAsync(connectionString, jobName)` — executes `msdb.dbo.sp_stop_job`
- `EnableDisableJobAsync(connectionString, jobName, bool enabled)` — executes `msdb.dbo.sp_update_job @enabled = 0/1`
- `UpdateJobAsync(connectionString, jobName, newName, description, enabled, categoryId)` — wraps `sp_update_job`
- `AddJobScheduleAsync(connectionString, jobName, scheduleParams)` — wraps `sp_add_jobschedule`
- `UpdateJobScheduleAsync(connectionString, jobName, scheduleId, scheduleParams)` — wraps `sp_update_jobschedule`
- `DeleteJobScheduleAsync(connectionString, jobName, scheduleId)` — wraps `sp_delete_jobschedule`
- `GetJobCategoriesAsync(connectionString)` — queries `msdb.dbo.syscategories`
- `AddJobStepAsync(connectionString, jobName, stepName, subsystem, command, database)` — wraps `sp_add_jobstep`
- `UpdateJobStepAsync(connectionString, jobName, stepId, stepName, command)` — wraps `sp_update_jobstep`
- `DeleteJobStepAsync(connectionString, jobName, stepId)` — wraps `sp_delete_jobstep`

- `FormatJobSchedule()` — static helper to turn schedule columns into human-readable strings

**New views/files:**
- `Views/ActivityView.axaml(.cs)` — the Activity tab content with sub-tabs (Sessions + Jobs)
- Consider `ActivityViewModel.cs` since this view has enough state to warrant a proper VM (two sub-tabs, auto-refresh timer, selected job detail panel state, filter states)



## 5. Implementation Priority

1. **Active Sessions grid** — core DMV query, grid display, manual refresh
2. **Kill Session** — with confirmation dialog and self-kill prevention
3. **Auto-refresh toggle** — timer-based polling, shared between both tabs
4. **Blocking indicators** — colored blocking session column, clickable
5. **Jobs Dashboard grid** — expanded query with categories, schedules, status, color coding
6. **Human-readable schedule formatting** — `FormatJobSchedule()` helper
7. **Job detail panel — General + History tabs** — inline editing for job name/description/enabled/category, history grid
8. **Job detail panel — Steps tab** — view, edit, add, delete steps inline
9. **Job detail panel — Schedule tab** — full schedule editor with frequency/day/time pickers
10. **Start/Stop/Enable/Disable Job** — buttons with confirmation dialogs
11. **Jobs filters** — text search, status dropdown, category dropdown
12. **Blocking chain visualization** — text-based chain display (nice to have)




