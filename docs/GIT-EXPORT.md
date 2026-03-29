# Git Export — Feature Spec

Export all database object definitions as `.sql` files to a local folder (typically a git repo). Each export is a full snapshot — the folder always reflects the current state of the server.

---

## 1. How It Works

**Trigger:** Button in Settings (next to the git path field) or File → Export to Git. Manual only — no automatic exports.

**Flow:**
1. User clicks Export
2. App connects to the server and enumerates all databases
3. For each database, pulls all object definitions
4. Writes each object as an individual `.sql` file in the configured folder
5. Compares against previous files — detects new, modified, deleted
6. Removes `.sql` files for objects that no longer exist (folder stays clean)
7. Appends a summary to `CHANGELOG.md` at the export root
8. Shows a summary dialog: X new, Y modified, Z deleted, took N seconds

---

## 2. Folder Structure

```
/configured-export-path/
  CHANGELOG.md
  /ServerName/
    /DatabaseName1/
      /Tables/
        dbo.Employees.sql
        dbo.Orders.sql
      /Views/
        dbo.vw_ActiveOrders.sql
      /StoredProcedures/
        dbo.usp_GetStock.sql
        dbo.usp_UpdateOrder.sql
      /Functions/
        dbo.fn_CalcTotal.sql
      /Triggers/
        dbo.tr_AuditLog.sql
    /DatabaseName2/
      /Tables/
        ...
```

**File naming:** `schema.objectname.sql` (e.g. `dbo.usp_GetStock.sql`). Dots in object names replaced with underscores to avoid filesystem issues.

**Server name:** Use the connection's server address, sanitized for filesystem (replace `\` with `_` for named instances, strip port numbers or keep as `server_1434`).

---

## 3. What Gets Exported

**Code objects** (from `sys.sql_modules` joined with `sys.objects`):
- Stored Procedures
- Functions (scalar, table-valued, inline)
- Views
- Triggers (DML and DDL)

**Tables** (generated CREATE TABLE script from metadata):
- Column definitions with data types, nullability, defaults
- Primary keys and unique constraints
- Foreign keys
- Indexes
- Check constraints
- Query `INFORMATION_SCHEMA.COLUMNS`, `sys.indexes`, `sys.index_columns`, `sys.foreign_keys`, `sys.foreign_key_columns`, `sys.default_constraints`, `sys.check_constraints`

**Databases:** All user databases by default. System databases (master, msdb, tempdb, model) included but skippable via a checkbox in Settings: "Include system databases" (default: off).

**Excluded:** Logins, server-level objects, database settings, permissions, schemas (for now — these could be added later).

---

## 4. Change Detection

Before writing a file, compare the new content with the existing file on disk:
- **New:** File doesn't exist yet → write it, log as "added"
- **Modified:** File exists but content differs → overwrite it, log as "modified"  
- **Unchanged:** File exists and content matches → skip it, don't log
- **Deleted:** File exists on disk but object no longer exists on server → delete the file, log as "deleted"

Use simple string comparison on the file content. No need for diffing — git handles that.

**Cleanup:** After export, scan the export folders for `.sql` files that don't correspond to any current object. Delete them. This keeps the folder in sync — if someone drops a proc, the file disappears on next export.

---

## 5. Changelog

`CHANGELOG.md` at the export root. Appended on each export run (never overwritten).

**Format:**
```markdown
## Export — 2026-03-30 14:22:15

**Server:** localhost,1434 (sa)
**Databases:** 5 (3 with changes)
**Duration:** 12.4s

### Changes
- **Added:** TestDB/StoredProcedures/dbo.usp_NewProc.sql
- **Modified:** TestDB/StoredProcedures/dbo.usp_GetStock.sql
- **Modified:** OrdersDB/Views/dbo.vw_ActiveOrders.sql
- **Deleted:** TestDB/Functions/dbo.fn_OldCalc.sql

**Summary:** 1 added, 2 modified, 1 deleted

---
```

If no changes detected, still log the run but note "No changes detected."

---

## 6. UI

**Settings panel** (already has the path field):
- Git export path: text field (already exists)
- Browse button to pick folder
- "Include system databases" checkbox (default: off)
- "Export Now" button — triggers the export

**Progress:** Show a progress dialog during export — "Exporting DatabaseName... (3/12)" with a progress bar. Export can take a few seconds for large servers.

**Summary dialog:** After export completes, show a brief summary: databases scanned, objects exported, changes detected, time taken. "Open Folder" button to open the export path in Finder/Explorer.

---

## 7. Implementation Notes

**Queries per database:**
```sql
-- Code objects (procs, functions, views, triggers)
SELECT s.name AS SchemaName, o.name AS ObjectName, o.type_desc, m.definition
FROM sys.sql_modules m
JOIN sys.objects o ON m.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE o.is_ms_shipped = 0

-- Tables: use INFORMATION_SCHEMA + sys catalog views to build CREATE TABLE scripts
-- (reuse the same logic from Script Object As CREATE for tables)
```

**Key detail:** Reuse the existing CREATE TABLE script generation from Script Object As (Section 6 of TOOLS-MENU.md). Don't duplicate that logic — call the same method.

**Error handling:**
- If a database is inaccessible (permissions), skip it and log a warning in the changelog
- If the export path doesn't exist, create it
- If a file write fails, log the error and continue with other objects

**Performance:** Use `Task.Run` to avoid blocking the UI. One database at a time is fine — no need for parallel database queries.

---

## Implementation Priority

This is a single feature — implement it as one unit. The pieces in order:
1. Wire up the "Export Now" button in Settings
2. Database enumeration + object query loop
3. File writing with folder structure creation
4. Change detection (compare before write)
5. Cleanup of deleted objects
6. Changelog generation
7. Progress dialog
8. Summary dialog with "Open Folder"
