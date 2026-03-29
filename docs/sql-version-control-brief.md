# SQL Version Control App - Project Brief

## Overview
A desktop GUI application for tracking and viewing SQL Server database object changes (procedures, functions, tables, etc.). Similar to StarTeam but for database objects. Changes are captured automatically by a DDL trigger - the app is a viewer/browser for that history with diff and rollback capabilities.

## Tech Stack
**Avalonia UI** with C# (.NET 8)
- Cross-platform (Windows + Mac)
- Looks like a native desktop app
- Use AvaloniaEdit for syntax highlighting and diff views

## Data Source
A SQL Server DDL trigger already captures all DDL events into a log table. The app reads from this.

### Current DDL Log Structure (semicolon delimited, multi-line SQL):
```
ID;ServerName;DatabaseName;EventType;ObjectType;SchemaName;ObjectName;SQLStatement;HostName;Login;IPAddress;ApplicationName;...;Timestamp
```

### Proposed Clean Schema (app should create/manage this):
```sql
CREATE TABLE dbo.ObjectVersions (
    VersionId INT IDENTITY PRIMARY KEY,
    DatabaseName NVARCHAR(128),
    SchemaName NVARCHAR(128),
    ObjectName NVARCHAR(128),
    ObjectType NVARCHAR(50),      -- PROCEDURE, FUNCTION, TABLE, VIEW, TRIGGER, INDEX
    Definition NVARCHAR(MAX),
    EventType NVARCHAR(50),       -- CREATE, ALTER, DROP
    ChangedBy NVARCHAR(128),      -- Login name
    HostName NVARCHAR(128),
    IPAddress NVARCHAR(50),
    AppName NVARCHAR(256),
    ChangedAt DATETIME2,
    VersionNumber INT,            -- Auto-increment per object, based on DDL event order (NOT parsed from code comments)
    INDEX IX_Object (DatabaseName, SchemaName, ObjectName),
    INDEX IX_ChangedAt (ChangedAt DESC)
)
```

App needs an ETL job or service to parse the raw DDL log and populate this clean table. Filter out noise like:
- SQL Telemetry (EVENT_SESSION stuff)
- Temp tables (names starting with # or tmp_)
- UPDATE_STATISTICS
- xp_cmdshell enable/disable

## Core Features

### 1. Object Browser (Left Panel)
- Tree view: Database → Schema → Object Type → Objects
- Show object count per node
- Filter/search box at top
- Icons per object type

### 2. Recent Changes Feed (Main Panel - Default View)
- List of recent changes, newest first
- Each row shows: ObjectName, EventType, ChangedBy, HostName, Timestamp
- Click to open history for that object
- Filter by: Date range, User, Database, Object type

### 3. Object History View
- When you select an object, show all versions in a list
- Columns: Version #, Event Type, Changed By, Timestamp
- Select a version to see full definition with syntax highlighting

### 4. Diff View
- Select two versions (checkboxes or ctrl+click)
- Side-by-side diff with:
  - Syntax highlighting
  - Line numbers
  - Red/green highlighting for removed/added lines
- Or right-click a version → "Compare with previous"

### 5. Rollback
- Right-click a version → "Rollback to this version"
- Shows preview of the ALTER statement it will run
- Confirmation dialog with warning
- Executes the ALTER/CREATE statement
- Requires appropriate permissions

### 6. Context Menu Options
- Compare with previous version
- Compare with selected version
- Copy definition to clipboard
- Rollback to this version
- View in SSMS (opens SSMS with object selected, if possible)

## UI Layout
```
┌─────────────────────────────────────────────────────────────────┐
│  [Database Dropdown]  [Search Box]  [Date Filter]  [Refresh]    │
├──────────────┬──────────────────────────────────────────────────┤
│              │  Recent Changes                                   │
│  Object      │  ┌─────────────────────────────────────────────┐ │
│  Browser     │  │ Object       Event   User    Host  Timestamp│ │
│              │  │ ──────────────────────────────────────────  │ │
│  ▼ AAD       │  │ usp_convey   ALTER   HJS    NB209  Dec 16   │ │ ← Click a row
│    ▼ dbo     │  │ usp_ship_a   ALTER   HJS    NB209  Dec 16   │ │
│      ▼ Procs │  │ usp_pack_g   ALTER   HJS    APP06  Dec 15   │ │
│        usp_a │  │ ...                                         │ │
│        usp_b │  └─────────────────────────────────────────────┘ │
│      ▼ Funcs │                                                  │
│      ▼ Views ├──────────────────────────────────────────────────┤
│    ▼ custom  │  [Version: v6 ▼]              [Version: v7 ▼]    │ ← Dropdowns to pick versions
│  ▼ ADV       │  ┌────────────────────┬────────────────────────┐ │
│              │  │ -- v6 Dec 10       │ -- v7 Dec 16           │ │
│              │  │                    │                        │ │
│              │  │ IF @flag = 1       │ IF @flag = 1           │ │
│              │  │ BEGIN              │ BEGIN                  │ │
│              │  │-  SET @x = 5      -│+  SET @x = 10         +│ │ ← Red/green diff
│              │  │   ...              │   ...                  │ │
│              │  │ END                │ END                    │ │
│              │  └────────────────────┴────────────────────────┘ │
└──────────────┴──────────────────────────────────────────────────┘
```

### Diff Panel Behavior
- When you click a row in Recent Changes, bottom panel auto-loads: **previous version** (left) vs **clicked version** (right)
- Two dropdowns above the diff let you pick any two versions to compare (versions are numbered by capture order in DDL log: v1, v2, v3...)
- Syntax highlighting + line numbers + red/green for removed/added lines
- Synchronized scrolling between left and right panels

## Connection Settings
- Store connection string securely (Windows Credential Manager or encrypted config)
- Support Windows Auth and SQL Auth
- Remember last connection
- Connection dialog on first launch

## Nice to Haves (Phase 2)
- Export object history to file
- Notifications when specific objects change
- Tag/label versions (e.g., "Production Dec 15")
- Compare objects across databases
- Dark mode

## Technical Notes
- Version numbers are auto-generated by the app based on DDL event sequence per object (first captured = v1, next = v2, etc). Do NOT try to parse version comments from the SQL code - many old sprocs don't have them.
- The DDL trigger logs are on server GRTDPHJDB006 / GRTDPHJDB61 (load balanced)
- Main database with changes is AAD
- Users connect with shared account HJS, so "ChangedBy" often just says HJS
- HostName and IPAddress are more useful for identifying who made changes
- DDL trigger currently missing FUNCTION events - needs to be added separately

## Getting Started
1. Set up Avalonia project with MVVM pattern
2. Create the ObjectVersions table schema
3. Build connection settings dialog
4. Build the object browser tree
5. Build the history list view
6. Add syntax-highlighted code view (AvaloniaEdit)
7. Add diff view
8. Add rollback functionality
