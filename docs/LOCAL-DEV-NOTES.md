# Local Development Setup

## Docker SQL Server (Two-Server Setup)

Two SQL Server containers simulate PROD and DEV environments. Both databases are named `TestDB` — same name, different servers. This lets you test Database Compare by connecting to two different servers rather than two databases on the same server.

### Server 1 — PROD (port 1433)

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Omer0370!" -e "MSSQL_AGENT_ENABLED=true" \
  -v sqldata1:/var/opt/mssql -p 1433:1433 --name zealous_cannon \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

- **Host:** localhost,1433
- **User:** sa
- **Password:** Omer0370!
- **Container:** zealous_cannon (`b370073ec7f5`)
- **Volume:** `sqldata1`
- SQL Agent enabled (for job testing)

### Server 2 — DEV (port 1434)

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Omer0370!" \
  -v sqldata2:/var/opt/mssql -p 1434:1433 --name wonderful_ellis \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

- **Host:** localhost,1434
- **User:** sa
- **Password:** Omer0370!
- **Container:** wonderful_ellis (`fd62dee2e224`)
- **Volume:** `sqldata2`
- No SQL Agent needed

### Important
- `-v sqldataN:/var/opt/mssql` — named volumes so data persists across container restarts and replacements
- `-e MSSQL_AGENT_ENABLED=true` — enables SQL Agent (Server 1 only, for job testing)
- If you need to enable Agent on an already running container: `docker exec -it <container_id> /opt/mssql/bin/mssql-conf set sqlagent.enabled true` then `docker restart <container_id>`
- **Never spin up a new container without the volume mount** — you'll lose all test data

### Useful Docker Commands
```bash
# Check if containers are running
docker ps

# Stop/start
docker stop zealous_cannon wonderful_ellis
docker start zealous_cannon wonderful_ellis

# Kill and remove
docker rm -f zealous_cannon
docker rm -f wonderful_ellis
```

## First Time Setup

1. Run both docker commands above
2. Wait ~20 seconds for SQL Server to initialize
3. Seed both servers:

```bash
# Server 1 (PROD) — tables, data, views, procs, functions, trigger, Agent jobs
docker cp scripts/seed-server1.sql zealous_cannon:/tmp/seed-server1.sql
docker exec zealous_cannon /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Omer0370!" -C -i /tmp/seed-server1.sql

# Server 2 (DEV) — same DB name, different schema for Compare testing
docker cp scripts/seed-server2.sql wonderful_ellis:/tmp/seed-server2.sql
docker exec wonderful_ellis /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Omer0370!" -C -i /tmp/seed-server2.sql
```

### What the seed scripts create

**Server 1 — PROD** (`scripts/seed-server1.sql`):
- **TestDB** with 7 tables (Departments, Employees, Projects, ProjectAssignments, EmployeeSalaryHistory, AuditLog, HeartbeatLog)
- 3 views, 4 stored procedures, 2 functions, 1 trigger
- 15 employees across 6 departments, 6 projects with assignments
- 6 SQL Agent jobs:
  - `Test - Quick Heartbeat` — every 1 min (generates history fast)
  - `Test - DB Maintenance` — every 5 min, 2 steps (update stats + CHECKDB)
  - `Test - Legacy Cleanup` — disabled (tests enabled/disabled display)
  - `Test - Nightly Report` — daily at 02:00
  - `Test - Broken Import` — every 3 min, always fails (tests failed jobs badge)
  - `Test - Intermittent Fail` — every 2 min, ~50% failure rate (tests mixed history)

**Server 2 — DEV** (`scripts/seed-server2.sql`):
- **TestDB** with intentional schema differences from PROD:
  - Missing tables: EmployeeSalaryHistory, AuditLog, HeartbeatLog
  - Extra table: OfficeLocations
  - Modified: vw_ProjectOverview (extra columns), usp_SearchEmployees (extra param), fn_GetDepartmentHeadcount (no IsActive filter)
  - Missing: vw_DepartmentStats, usp_UpdateEmployeeSalary, usp_GetProjectTeam, trg_Employees_Audit
  - Extra: vw_OfficeCapacity, fn_FormatEmployeeName, usp_GetOfficeLocations
- No SQL Agent jobs

## Build & Run

```bash
dotnet run -f net10.0
```

## Notes
- This is a local dev environment only. No real data.
- Named volumes (`sqldata1`, `sqldata2`) ensure data persists across container restarts and replacements. **Never omit these.**
- Each SQL Server container uses ~2GB RAM (~4GB total for both).
- The platform warning about linux/amd64 vs linux/arm64 is normal on Apple Silicon — it runs under Rosetta emulation.
