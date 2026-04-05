# Server Health Monitoring

## The Problem
The Activity tab has 4 real-time stat cards (CPU, Memory, Buffer Cache, TempDB) but they're point-in-time only. When you open Lookout at 4am because the warehouse is down, you need to see what happened an hour ago — not what's happening right now. The app won't have been running an hour ago, so in-memory history is useless.

## Two-Tier Approach

### Tier 1: Ring Buffer History (No setup needed)
SQL Server already keeps ~4 hours of CPU history in `sys.dm_os_ring_buffers` (scheduler monitor). This data exists on every SQL Server instance with no configuration required.

**What we get for free:**
- CPU % over the last ~4 hours (256 data points, ~1 per minute)
- Already queried by the existing Activity tab for the current reading — just need to pull the full history

**What we don't get:**
- Memory history (ring buffers have it but it's less reliable)
- No wait stats, no I/O, no blocking history, no custom metrics
- Only ~4 hours of lookback

**UX:** A mini sparkline or area chart behind/below each stat card. CPU card shows the current value AND a 4-hour trend line. This is immediate value with zero setup.

**Query:**
```sql
-- CPU history from ring buffers (last ~4 hours)
SELECT
    record_id,
    DATEADD(ms, -1 * (ts_now - [timestamp]), GETDATE()) AS EventTime,
    SQLProcessUtilization AS SqlCpu,
    100 - SystemIdle - SQLProcessUtilization AS OtherCpu,
    SystemIdle
FROM (
    SELECT
        record.value('(./Record/@id)[1]', 'int') AS record_id,
        record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int') AS SystemIdle,
        record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') AS SQLProcessUtilization,
        [timestamp],
        (SELECT cpu_ticks / (cpu_ticks / ms_ticks) FROM sys.dm_os_sys_info) AS ts_now
    FROM (
        SELECT [timestamp], CONVERT(xml, record) AS record
        FROM sys.dm_os_ring_buffers
        WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
        AND record LIKE '%<SystemHealth>%'
    ) AS x
) AS y
ORDER BY record_id DESC
```

### Tier 2: Collector Job (Admin setup — longer history, more metrics)
A lightweight SQL Agent job that runs every 1-2 minutes and INSERTs key metrics into a table. This is the "DDL trigger equivalent" for health monitoring — requires admin setup but gives much richer data.

**Setup:** Admin clicks "Enable Health Monitoring" in Settings. Lookout creates:
1. A `LookoutHealthMetrics` table (on the target server, in a configurable database)
2. A SQL Agent job `Lookout - Health Collector` that runs every 1 minute

**Table schema:**
```sql
CREATE TABLE dbo.LookoutHealthMetrics (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    CollectedAt     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    SqlCpuPercent   TINYINT,
    OtherCpuPercent TINYINT,
    MemoryUsedMB    INT,
    MemoryAvailMB   INT,
    BufferCacheHit  DECIMAL(5,2),
    TempDbUsedMB    INT,
    TempDbTotalMB   INT,
    ActiveSessions  SMALLINT,
    BlockedSessions SMALLINT,
    LongestQuerySec INT,
    TopWaitType     NVARCHAR(60),
    TopWaitTimeMs   BIGINT,
    BatchRequestsSec INT,   -- from dm_os_performance_counters delta
    PageLifeExpSec  INT     -- from dm_os_performance_counters

    INDEX IX_CollectedAt NONCLUSTERED (CollectedAt)
);
```

**Collector job step (T-SQL):**
```sql
-- Single INSERT pulling from multiple DMVs
INSERT INTO dbo.LookoutHealthMetrics (
    SqlCpuPercent, OtherCpuPercent,
    MemoryUsedMB, MemoryAvailMB,
    BufferCacheHit, TempDbUsedMB, TempDbTotalMB,
    ActiveSessions, BlockedSessions, LongestQuerySec,
    TopWaitType, TopWaitTimeMs, PageLifeExpSec
)
SELECT
    -- CPU from ring buffer (latest)
    cpu.SQLProcessUtilization,
    100 - cpu.SystemIdle - cpu.SQLProcessUtilization,
    -- Memory
    mem.physical_memory_in_use_kb / 1024,
    mem.available_physical_memory_kb / 1024,
    -- Buffer cache hit ratio
    CAST(CAST(bch.cntr_value AS FLOAT) / NULLIF(bchb.cntr_value, 0) * 100 AS DECIMAL(5,2)),
    -- TempDB
    tdb.used_mb, tdb.total_mb,
    -- Sessions
    sess.active_count, sess.blocked_count, sess.longest_sec,
    -- Top wait
    waits.wait_type, waits.wait_time_ms,
    -- PLE
    ple.cntr_value
FROM (
    -- CPU subquery (latest ring buffer entry)
    ...
) cpu
CROSS JOIN sys.dm_os_process_memory mem
CROSS JOIN (...) -- other subqueries
```

**Retention:** Auto-cleanup — delete rows older than N days (configurable in Settings, default 7 days). The collector job itself handles cleanup at the end of each run: `DELETE FROM LookoutHealthMetrics WHERE CollectedAt < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME())`

**Storage estimate:** ~150 bytes per row × 1 per minute × 1440/day × 7 days ≈ 1.5 MB. Negligible.

## UX Design

### Activity Tab Changes

**Current layout:**
```
[SQL CPU 0%] [Memory 4.0 GB] [Buffer Cache 100%] [TempDB 1 MB]
             [Sessions Grid]
```

**With Tier 1 (no setup):**
Each stat card gets a mini sparkline underneath showing the last 4 hours of that metric (CPU only from ring buffers initially). Clicking a stat card could expand it into a larger chart view.

**With Tier 2 (collector enabled):**
- A new "Health" sub-tab alongside Sessions and Jobs (or replace the stat cards area with a richer dashboard)
- Time range selector: Last 1h / 4h / 12h / 24h / 7d
- Area charts for: CPU, Memory, TempDB, Active Sessions, Blocked Sessions
- Wait stats breakdown (top 5 wait types over time)
- "What happened?" quick view: any period where CPU > 80% or blocked sessions > 0 gets highlighted on the timeline

### The 4am Workflow
1. Open Lookout, connect to server
2. Activity tab → Health sub-tab
3. See CPU spike at 3:47am, blocking spike at same time
4. Click the spike → Sessions grid filters to that time window (if collector captured session data)
5. Or at minimum: see the timeline, know when the problem started, correlate with Jobs tab (did a job fail at 3:47?)

## Settings Integration

**Admin mode only** (depends on Task 4 from SESSION-2026-04-05.md):
- "Health Monitoring" section in Settings
- Toggle: Enable/Disable collector
- Database dropdown: where to create the metrics table
- Collection interval: 1 min (default) / 2 min / 5 min
- Retention: 7 days (default) / 14 / 30
- "Set Up Now" button → creates table + Agent job
- "Remove" button → drops job + optionally drops table
- Status indicator: "Collector running — last entry 45 seconds ago" or "Collector not configured"

Same pattern as DDL trigger setup — Admin configures it, Normal users just see the charts.

## Implementation Priority
1. **Tier 1 sparklines** — ring buffer CPU history behind the existing stat card. Zero setup, immediate value.
2. **Settings UI** for collector setup (Admin mode)
3. **Collector job creation** — `sp_add_job` with the metrics INSERT
4. **Health sub-tab** with time-range charts reading from the metrics table
5. **Blocking/session correlation** — clicking a spike shows relevant sessions (stretch goal)

## Dependencies
- Task 4 (Admin/Normal user mode) should land first
- Charting: need a chart control in Avalonia. Options: OxyPlot.Avalonia, LiveCharts2, or custom SVG/Canvas rendering. Research needed.
- The collector job creation reuses the same `sp_add_job` / `sp_add_jobstep` / `sp_add_jobschedule` pattern from the seed scripts

## Not In Scope (Future)
- Alerting / notifications (email/push when CPU > threshold)
- Disk I/O latency monitoring
- Index fragmentation tracking
- Comparing health across multiple servers
- Exporting health reports
