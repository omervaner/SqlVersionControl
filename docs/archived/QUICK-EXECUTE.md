# Quick Execute — Option+Click on Proc Names

**Created:** March 30, 2026
**Shortcut:** Option+Click (Mac) / Alt+Click (Windows) on a stored procedure name in the editor

---

## What It Does

Option+Click a stored procedure name anywhere in the editor → app fetches its parameters → opens a **new query tab** with a ready-to-run execution template:

```sql
DECLARE @CustomerId INT = NULL
DECLARE @StartDate DATETIME = NULL
DECLARE @Status NVARCHAR(50) = NULL

EXEC [dbo].[usp_GetCustomerOrders]
    @CustomerId = @CustomerId,
    @StartDate = @StartDate,
    @Status = @Status
```

Fill in the DECLARE values, hit F5. That's it.

---

## Why

You're reading a 200-line query, you see `usp_GetCustomerOrders` referenced in a comment or a nested call. You want to test it right now with your own parameters. Currently you'd have to:
1. Find the proc in Object Explorer
2. Right-click → Generate EXEC
3. Manually figure out the parameter types
4. Write DECLARE statements

Option+Click collapses all of that into one gesture.

---

## Behavior

1. User holds Option (Mac) or Alt (Windows) and clicks a word in the editor
2. App extracts the word under cursor (same word-detection logic as Peek Definition / Cmd+Click)
3. App calls `GetProcParametersAsync(database, schema, procName)` to fetch parameter names and types
4. If the object is found and has parameters → open a new query tab with the template
5. If the object is found but has zero parameters → open a new tab with just `EXEC [schema].[procName]`
6. If the object is not found → brief flash message: "Object not found" (same flash pattern as query execution)
7. New tab inherits the current tab's database selection and connection

## Template Format

```sql
DECLARE @ParamName TYPE = NULL
```

One DECLARE per line, each defaulting to NULL. Then a blank line, then the EXEC block with named parameters.

Type formatting should use `SqlTypeFormatter.Format()` for consistent output (e.g., `NVARCHAR(50)` not `nvarchar`, `DECIMAL(18,2)` not `decimal`).

For OUTPUT parameters, mark them:
```sql
DECLARE @ResultCount INT = NULL  -- OUTPUT

EXEC [dbo].[usp_ProcessBatch]
    @BatchId = @BatchId,
    @ResultCount = @ResultCount OUTPUT
```

## What Already Exists

All the building blocks are in place:

- **Word detection under cursor:** `Peek Definition` (Section 9 of TOOLS-MENU.md) already does this for Cmd+Click. Same logic, different modifier key.
- **Parameter fetching:** `DatabaseService.GetProcParametersAsync()` returns `List<(string Name, string TypeName)>` — already used by OE context menu "Generate EXEC".
- **Type formatting:** `SqlTypeFormatter.Format()` for proper type display.
- **New tab creation:** `QueryEditorHost` already supports programmatic tab creation with preset SQL text and database selection.
- **Flash messages:** `QueryFlash` event on `QueryTabViewModel`.

## Implementation

The click handler in `QueryTabView.axaml.cs` (or wherever Cmd+Click is handled for Peek Definition) needs a second branch:

```
if (Cmd/Ctrl held) → Peek Definition (existing)
if (Option/Alt held) → Quick Execute (new)
```

The template generation is a simple string builder — no new service needed. Could be a static method on `DataEditService` or a new small helper, but honestly it's ~20 lines of code that can live right in the click handler or ViewModel.

## Keyboard Shortcut (Optional)

If we want a non-mouse alternative: **Cmd+Shift+E** (Mac) / **Ctrl+Shift+E** (Windows) — "Execute template for word at cursor." Lower priority than the click gesture.

---

## Tab Title

The new tab should be titled with the proc name: `usp_GetCustomerOrders` rather than the generic "Query N". This makes it obvious what you're testing when you have multiple tabs open.
