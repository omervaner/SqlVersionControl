# Local Development Setup

## Docker SQL Server (Local)

Run this to spin up a local SQL Server instance for development:

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Omer0370!" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

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
2. Wait ~10 seconds for SQL Server to initialize
3. Connect from the app or `sqlcmd` using the credentials above
4. Create a test database and seed some sample objects (tables, sprocs, views) for testing

## Build & Run

```bash
dotnet run
```

## Notes
- This is a local dev environment only. No real data.
- The Docker container does not persist data by default. If you remove the container, the databases are gone. To persist, add a volume mount.
- SQL Server in Docker uses ~2GB RAM.
