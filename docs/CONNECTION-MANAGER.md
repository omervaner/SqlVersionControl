# Connection Manager + Multi-Connection Object Explorer

**Created:** March 30, 2026
**Updated:** March 30, 2026 — added architecture layer and multi-connection OE spec

---

## Overview

Central place to manage all database connections, plus a refactored Object Explorer that shows multiple connected servers in one tree. Currently connections are scattered across the connection dialog, compare tab dropdowns, recent connections list, and per-tab quick-switch. With 20+ connections across dev/test/staging/prod, this is unmanageable.

---

## Architecture — ConnectionRegistry

### The Key Insight

`DatabaseService` already supports multi-connection. Almost every method has two overloads — one using the internal `_connectionString`, one taking an explicit connection string parameter. The per-tab system already proved this works: each `QueryTabViewModel` carries `TabConnectionString` and passes it explicitly.

What's missing is a **registry layer** that sits above `DatabaseService`:

```
┌─────────────────────────────┐
│     ConnectionRegistry      │  ← NEW: manages which connections exist
│  - List<ManagedConnection>  │     resolves credentials, hands out connection strings
│  - Connect / Disconnect     │
│  - GetConnectionString(id)  │
└──────────────┬──────────────┘
               │ passes connection strings down
┌──────────────▼──────────────┐
│       DatabaseService       │  ← UNCHANGED: already has per-connection-string overloads
│  - GetTablesAsync(connStr)  │     doesn't care where the string came from
│  - GetViewsAsync(connStr)   │
│  - ExecuteQueryAsync(...)   │
└─────────────────────────────┘
```

`DatabaseService` doesn't change. The registry manages WHICH connection strings exist. Everything else (OE, tabs, compare, activity) asks the registry for a connection string by ID and passes it to `DatabaseService`.

### ConnectionRegistry Service

```csharp
public class ConnectionRegistry
{
    // All registered connections (persisted in settings.json)
    public ObservableCollection<ManagedConnection> Connections { get; }

    // Currently active (connected) connections
    public IEnumerable<ManagedConnection> ActiveConnections
        => Connections.Where(c => c.IsConnected);

    // Events
    public event Action<ManagedConnection>? ConnectionAdded;
    public event Action<ManagedConnection>? ConnectionRemoved;
    public event Action<ManagedConnection>? ConnectionStateChanged;  // connected/disconnected

    // Resolve a full connection string (with password from PasswordStore)
    public string? GetConnectionString(string connectionId);

    // Connect — resolves password, tests, marks active
    // Returns (success, error) — prompts for password via event if needed
    public async Task<(bool Success, string? Error)> ConnectAsync(string connectionId);

    // Disconnect — clears pools, marks inactive
    public void Disconnect(string connectionId);

    // CRUD
    public ManagedConnection Add(SavedConnection config);
    public void Remove(string connectionId);
    public void Update(string connectionId, SavedConnection config);

    // Persistence
    public void Save();  // writes to settings.json
    public void Load();  // reads from settings.json + migrates legacy entries

    // Password prompt — View subscribes to this
    public event Func<SavedConnection, Task<string?>>? PasswordRequested;
}

public class ManagedConnection
{
    public SavedConnection Config { get; set; }     // The persisted settings
    public bool IsConnected { get; set; }           // Live state
    public string? ResolvedConnectionString { get; set; }  // Cached after connect
    public DateTime? ConnectedAt { get; set; }
    public List<string>? Databases { get; set; }    // Cached after connect

    // Convenience
    public string Id => Config.Id;
    public string DisplayName => Config.Name;
    public string Color => Config.Color;
    public string Environment => Config.Environment;
    public bool IsProduction => Config.Environment == "Production";
}
```

### What This Replaces

| Currently | Becomes |
|-----------|---------|
| `DatabaseService._connectionString` + `SetConnection()` | Legacy — still works, but new code uses registry |
| `CompareViewModel._passwords` dictionary | `ConnectionRegistry.GetConnectionString(id)` — one place for credential resolution |
| `CompareViewModel.BuildConnectionString()` | Deleted — registry handles this |
| `MainWindowViewModel.OnConnected()` stamping 4 display properties | Registry fires `ConnectionStateChanged`, views subscribe |
| `SettingsService.RecentConnections` | `ConnectionRegistry.Connections` — single source of truth |
| Quick-switch buttons reading `SettingsService.GetNamedConnections()` | Quick-switch reads `ConnectionRegistry.ActiveConnections` |
| Per-tab `TabConnectionString` built manually | `TabConnectionId` → `ConnectionRegistry.GetConnectionString(id)` |

### Migration from Current Architecture

This is additive, not a rewrite. The migration path:

**Phase 1 — Registry exists, old code still works:**
- `ConnectionRegistry` wraps `SettingsService.RecentConnections` (reads/writes same data)
- `ConnectionDialog` uses the registry instead of `SettingsService` directly
- `DatabaseService._connectionString` still exists for backward compat
- OE still single-connection

**Phase 2 — OE goes multi-connection (see below)**

**Phase 3 — Clean up legacy:**
- Remove `DatabaseService._connectionString` and `SetConnection()`
- Remove `CompareViewModel._passwords` and `BuildConnectionString()`
- All connection string resolution goes through registry

---

## SavedConnection Data Model

```csharp
public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";              // Required — display name
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public string Color { get; set; } = "#88a1bb";
    public string Environment { get; set; } = "Unknown"; // Dev/Test/Staging/Production/Other/Unknown
    public bool TrustServerCertificate { get; set; } = true;  // SECURITY.md item 1.2
    public int SortOrder { get; set; }
}
```

### Migration for Existing Entries

On first load, existing `SavedConnection` entries won't have `Id` or `Environment`:
- Generate `Id = Guid.NewGuid().ToString()` for any with null/empty Id
- Default `Environment` to `"Unknown"` — not "Dev" (hides prod checks), not "Production" (spams dialogs)
- On first open of Connection Manager, show banner: "Classify your connections by environment for safety checks"

---

## Connection Manager Dialog

**Location:** File menu → "Manage Connections..." (Cmd+Shift+M)

### Layout

Left panel (list):
- All connections from `ConnectionRegistry.Connections`
- Each row: color dot + name + server/database + environment tag
- Connected ones show a green "connected" indicator
- Search/filter box at top
- Add / Delete buttons at bottom

Right panel (edit form):
- Connection Name (required)
- Server
- Database
- Authentication: Windows Auth toggle, Username, Password (masked)
- Environment: dropdown — Dev / Test / Staging / Production / Other
- Trust Server Certificate: checkbox (default on)
- Color: color picker
- Test Connection button with inline success/fail
- Save / Cancel buttons

### Password Handling

Passwords stay in `PasswordStore` (encrypted), never in the connection list. The Connection Manager stores identity (server/db/username). Passwords resolved via `PasswordStore.Get()` at connect time. Not found → prompt once → store.

`settings.json` remains safe to share — no passwords, ever.

---

## Multi-Connection Object Explorer

### Tree Structure

```
▼ 🔴 PROD WMS (10.0.0.15)
    ▼ GratisWMS
        ▼ Tables (247)
            ▼ dbo.as_master
                ▶ Columns (12)
                ▶ Triggers (2)
            ▶ dbo.inventory_detail
            ▶ dbo.order_header
            ...
        ▶ Views (18)
        ▶ Stored Procedures (342)
        ▶ Functions (27)
        ▶ Sequences (3)
        ▶ Jobs (15)
    ▶ GratisAudit
▼ 🟡 TEST WMS (10.0.0.20)
    ▶ GratisWMS
▼ 🔵 DEV Docker (localhost,1433)
    ▼ TestDB
        ...
```

Root level = connections from the registry, each with its color dot. Expand → that server's databases. Expand database → Tables/Views/Procs/Functions/Sequences/Jobs. Tables expand to show Columns and Triggers folders beneath each table.

### ObjectExplorerViewModel Changes

Currently `ObjectExplorerViewModel` holds a flat list of nodes under one database. Refactor to a tree:

```csharp
public class ObjectExplorerViewModel
{
    private readonly ConnectionRegistry _registry;
    private readonly DatabaseService _db;

    // Root nodes — one per active connection
    public ObservableCollection<OEConnectionNode> ConnectionNodes { get; }

    // Subscribes to registry.ConnectionStateChanged
    // When a connection is added/connected → add root node
    // When disconnected/removed → remove or grey out root node
}

public class OEConnectionNode
{
    public string ConnectionId { get; set; }
    public string DisplayName { get; set; }      // "PROD WMS (10.0.0.15)"
    public string Color { get; set; }
    public string Environment { get; set; }
    public ObservableCollection<OEDatabaseNode> Databases { get; set; }

    // Lazy-load: expand triggers GetDatabasesAsync(connectionString)
}

public class OEDatabaseNode
{
    public string ConnectionId { get; set; }     // Knows which connection it belongs to
    public string DatabaseName { get; set; }
    public ObservableCollection<OEFolderNode> Folders { get; set; }
    // Folders: Tables, Views, Stored Procedures, Functions, Sequences, Jobs
}

// ... OEFolderNode, OETableNode (with Columns + Triggers children), OEObjectNode, etc.
```

Every node carries its `ConnectionId`. When the user right-clicks → "SELECT TOP 100", the action calls `registry.GetConnectionString(node.ConnectionId)` and passes it to `DatabaseService`. No ambiguity about which server the action targets.

### Lazy Loading

Everything lazy-loads on expand (same as current OE behavior):
- Expand connection → `GetDatabasesAsync(connStr)`
- Expand database → `GetTablesAsync(connStr, db)`, `GetViewsAsync(connStr, db)`, etc.
- Expand table → `GetColumnsAsync(connStr, db, schema, table)` + trigger query
- Cache results per-node. Refresh button clears cache and reloads.

### Filter Box

The filter box at the top searches across ALL expanded connections. Type "inventory" → matches `dbo.inventory_detail` under PROD, `dbo.inventory_test` under DEV. Collapsed nodes aren't searched (you can't filter what hasn't been loaded).

### Actions — Which Connection?

Every OE action (double-click, right-click, drag-drop) resolves through the node's `ConnectionId`:

```csharp
// Example: double-click a table
void OnTableDoubleClicked(OETableNode node)
{
    var connStr = _registry.GetConnectionString(node.ConnectionId);
    // Open new query tab with this connection and database
    var tab = CreateNewTab(connStr, node.ConnectionId, node.DatabaseName);
    tab.ExecuteAsync($"SELECT TOP 100 * FROM [{node.Schema}].[{node.Name}]");
}
```

New query tabs opened from OE automatically get the correct connection — no manual switching.

### Drag-Drop Between Connections

Drag a table name from PROD, drop into a query tab connected to DEV. The text drops as `[schema].[tableName]` (same as today). The connection context comes from the TAB, not the dragged node. This is correct — you're writing a query against DEV that references a table name that also exists on PROD.

### Visual State

| State | Appearance |
|-------|-----------|
| Connected | Color dot, full opacity, expandable |
| Disconnected (was connected) | Grey dot, 40% opacity, "(offline)" suffix, nodes still visible but stale |
| Never connected | Color dot, "Click to connect" placeholder child |
| Connecting | Spinner replacing the expand arrow |
| Failed | Red dot, error tooltip, "Retry" in context menu |

---

## Startup Flow Change

### Current
App opens → ConnectionDialog → pick one connection → everything binds to it.

### New
App opens → ConnectionRegistry loads saved connections → auto-connect any connections marked "connect on startup" → OE populates root nodes for active connections. If no connections exist (first run), show Connection Manager dialog instead of the old ConnectionDialog.

Add a `ConnectOnStartup` bool to `SavedConnection` — user toggles it in the Connection Manager. Default: true for the first connection created, false for others.

---

## Integration Points

| Feature | Currently | With Registry |
|---------|-----------|---------------|
| **Connection Dialog (startup)** | Raw server/db fields | Shows saved connection list with Connect/Edit/New buttons. Double-click to connect. |
| **Compare tab** | Own dropdowns, own password dictionary | Dropdowns populated from `ConnectionRegistry.Connections`. No separate password management. |
| **Per-tab quick-switch** | Reads `SettingsService.GetNamedConnections()` | Reads `ConnectionRegistry.ActiveConnections` |
| **Activity Monitor** | Gets one connection string at init | Dropdown of active connections, switch between them |
| **Exec Plan** | Gets one connection from main view | Same — dropdown of active connections |
| **Version History** | Tied to `DatabaseService._connectionString` | Dropdown of active connections |
| **Production detection** | `Server.EndsWith(".15")` | `connection.IsProduction` from registry |
| **Settings** | Stores `RecentConnections` | Remove — registry is the source of truth |

---

## Implementation Order

1. **`ConnectionRegistry` service** — wraps existing `SettingsService.RecentConnections`, CRUD, credential resolution, `GetConnectionString(id)`. All existing code keeps working — registry reads/writes the same data.

2. **Connection Manager dialog** — list + edit form. Replaces the "Recent Connection" mode in `ConnectionDialog`.

3. **`ConnectionDialog` refactor** — startup dialog becomes a thin wrapper: shows the connection list from registry, Connect button calls `registry.ConnectAsync(id)`. "New Connection" button opens Connection Manager.

4. **Compare tab rewire** — replace `_passwords` dictionary and `BuildConnectionString()` with registry calls. Delete duplicate code.

5. **OE multi-connection** — refactor `ObjectExplorerViewModel` to tree-of-connections. Biggest piece of work, but registry is stable by now.

6. **Legacy cleanup** — remove `DatabaseService._connectionString` and `SetConnection()`, make all callers pass explicit connection strings from registry.

Steps 1-4 are mechanical and can ship incrementally. Step 5 is the big visual change. Step 6 is cleanup that can happen anytime after.
