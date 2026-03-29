# Connection Manager

**Created:** March 30, 2026
**Location:** File menu → "Manage Connections..."

---

## Overview

Central place to view, organize, edit, and delete all database connections. Currently connections are scattered across the connection dialog, compare tab dropdowns, recent connections list in settings, and OE per-tab quick-switch. With 20+ connections across dev/test/staging/prod environments, this is unmanageable without a dedicated screen.

---

## Location

**File menu** → "Manage Connections..." (Cmd+Shift+M or similar).

Connections are a top-level app concern, not a settings sub-feature. SSMS, DBeaver, DataGrip all surface connection management at the top level. Settings is for preferences (theme, fonts, grid density).

---

## Dialog Layout

Left panel (list):
- All saved connections in a scrollable list
- Each row: color dot + name + server/database + environment tag (Dev/Test/Staging/Prod)
- Search/filter box at top
- Add / Delete buttons at bottom
- Drag to reorder (optional — sort by name is fine for v1)

Right panel (edit form):
- Connection Name (required — no more unnamed connections)
- Server
- Database
- Authentication: Windows Auth toggle, Username, Password (masked)
- Environment: dropdown — Dev / Test / Staging / Production / Other
- Color: color picker (for tab dots and stripe)
- Test Connection button with inline success/fail indicator
- Save / Cancel buttons

---

## Data Model Changes

Extend `SavedConnection`:
```csharp
public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();  // Stable identity
    public string Name { get; set; } = "";           // Required — display name
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public string Color { get; set; } = "#88a1bb";   // Default blue-grey
    public string Environment { get; set; } = "Dev";  // Dev/Test/Staging/Production/Other
    public int SortOrder { get; set; }                // For manual ordering
}
```

The `Id` field gives each connection a stable identity so other parts of the app (session restore, compare last-used, per-tab connection) can reference it reliably even if the name changes.

### Migration for Existing Connections

Existing `SavedConnection` entries won't have `Id` or `Environment` fields. On first load:
- Generate `Id = Guid.NewGuid().ToString()` for any connection with null/empty Id
- Default `Environment` to `null` / "Unknown" — NOT "Dev" (would hide prod safety checks) and NOT "Production" (would spam every connection with scary dialogs)
- On first open of Connection Manager, show a banner: "Classify your connections by environment for safety checks"

---

## Integration Points

- **Connection Dialog (startup):** Show the saved connections list instead of raw server/database fields. "Connect" button, "Edit" button, "New" button. Double-click to connect.
- **Compare tab dropdowns:** Populate from saved connections. Remove the inline "Quick Add" workflow — use the Connection Manager instead.
- **Per-tab connection quick-switch:** The existing button strip pulls from saved connections.
- **Activity Monitor / Plan / History connection switches:** Same source.
- **Settings:** Remove the connection-related fields from Settings. Connection Manager is the single point of entry.
- **Production detection:** Use the `Environment` field instead of `Server.EndsWith(".15")` pattern matching. Confirmation dialogs key off `Environment == "Production"`.

---

## Password Handling

Passwords are NOT stored in the connection list — they stay in `PasswordStore` (encrypted). The Connection Manager stores the identity (server/db/username), and passwords are resolved via `PasswordStore.Get()` at connection time. If a password isn't found, prompt once and store it.

This means the settings.json file remains safe to accidentally share — no passwords, ever.
