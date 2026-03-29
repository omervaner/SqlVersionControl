# Local Development Setup

## Docker SQL Server (Local)


Run this to spin up a local SQL Server instance for development:

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Omer0370!" -e "MSSQL_AGENT_ENABLED=true" -v sqldata:/var/opt/mssql -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

**Important:**
- `-v sqldata:/var/opt/mssql` — named volume so data persists across container restarts and replacements
- `-e MSSQL_AGENT_ENABLED=true` — enables SQL Agent for job testing
- If you need to enable Agent on an already running container (without losing data): `docker exec -it <container_id> /opt/mssql/bin/mssql-conf set sqlagent.enabled true` then `docker restart <container_id>`
- **Never spin up a new container without the volume mount** — you'll lose all test data


### Connection Details
- **Host:** localhost,1433
- **User:** sa
- **Password:** Omer0370!

### Useful Docker Commands
```bash
# Check if container is running
docker ps

# Stop SQL Server
docker stop <container_id>

# Start it again
docker start <container_id>

# Kill and remove
docker rm -f <container_id>
```

## First Time Setup

1. Run the docker command above
2. Wait ~20 seconds for SQL Server to initialize
3. Connect from the app or `sqlcmd` using the credentials above
4. Run the seed script to create TestDB with sample data:

```bash
# Copy seed script into container and run it
docker cp /tmp/seed-testdb.sql <container_id>:/tmp/seed-testdb.sql
docker exec <container_id> /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Omer0370!" -C -i /tmp/seed-testdb.sql
```

The seed script (`/tmp/seed-testdb.sql`) creates:
- **TestDB** with 6 tables (Departments, Employees, Projects, ProjectAssignments, EmployeeSalaryHistory, AuditLog)
- 3 views, 4 stored procedures, 2 functions, 1 trigger
- 15 employees across 6 departments, 6 projects with assignments
- 4 SQL Agent test jobs:
  - `Test - Quick Heartbeat` — every 1 min (generates history fast)
  - `Test - DB Maintenance` — every 5 min, 2 steps (update stats + CHECKDB)
  - `Test - Legacy Cleanup` — disabled (tests enabled/disabled display)
  - `Test - Nightly Report` — daily at 02:00

## Build & Run

```bash
dotnet run -f net10.0
```

## Notes
- This is a local dev environment only. No real data.
- The `-v sqldata:/var/opt/mssql` volume mount ensures data persists across container restarts and replacements. **Never omit this.**
- SQL Server in Docker uses ~2GB RAM.
- The platform warning about linux/amd64 vs linux/arm64 is normal on Apple Silicon — it runs under Rosetta emulation.
