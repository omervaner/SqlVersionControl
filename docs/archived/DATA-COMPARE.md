# Data Compare — Feature Spec

Lives inside the existing Compare tab as the "Tables" mode (toggle already exists between Code and Tables).

---

## Flow

1. User selects source and target connections (already in Compare toolbar)
2. User searches/selects a table (e.g. "Employees") — dropdown or search box
3. App loads ALL rows from both source and target, matched by primary key
4. Master grid (top) shows every row with all columns, plus a status column

---

## Master Grid (Top Panel)

Shows all rows from both environments side by side, matched by PK.

**Columns:**
- **Status icon** (first column): visual indicator per row
  - ✓ green = identical on both sides
  - ≠ amber = row exists on both but has field differences
  - ← green = source only (doesn't exist in target)
  - → red = target only (doesn't exist in source)
- **PK column(s)**: the primary key value(s) used to match rows
- **All other columns**: show the SOURCE values by default. If a field differs from target, highlight it (amber background or text color)

**Column filter + search:** Above the grid, a dropdown to pick any column (e.g. "EmployeeId", "LastName", "Email") and a text input to search within that column. Typing "1" in the EmployeeId filter shows rows where EmployeeId contains "1" — so IDs 1, 11, 111, 10, etc. This is a contains/like filter, not exact match. Clearing the search shows all rows again.

**Row count summary:** "16 rows compared: 12 identical, 3 different, 1 source only, 0 target only" — shown above or below the grid.

---

## Detail Panel (Bottom Panel)

When user clicks a row in the master grid, the bottom panel expands (same pattern as results panel in Query Editor — collapsed by default, expands on click, splitter between).

Shows a **vertical field-by-field comparison** for the selected row:

| Column | Source Value | Target Value | Status |
|--------|-------------|-------------|--------|
| EmployeeId | 1 | 1 | Match |
| FirstName | Sarah | Sarah | Match |
| LastName | Chen | **Chang** | **Different** |
| Salary | 185000.00 | **190000.00** | **Different** |
| Phone | 555-0101 | *(NULL)* | **Different** |

- Matching fields: dimmed/muted text, no highlight
- Different fields: highlighted (amber), both values clearly visible, bold on the target value to show what's different
- NULL values: shown as italic "NULL" in the TextNull color
- Source-only rows: target column shows "—" or "Does not exist"
- Target-only rows: source column shows "—" or "Does not exist"

---

## Actions

**Per-field editing:** In the detail panel, user can click on a source or target value to edit it. This queues an UPDATE for that specific field.

**Deploy row:** Button to deploy the entire source row to target (generates INSERT if target-only, UPDATE if different, DELETE if source-only — user picks which direction).

**Deploy selected:** In the master grid, checkbox column to select multiple rows, then "Deploy Selected" button to push all selected rows from source to target.

**Preview SQL:** Before any deploy, show the generated SQL (INSERT/UPDATE/DELETE statements) in a preview dialog — same pattern as the Edit mode's "Show SQL" button in Query Editor.

---

## Implementation Notes

### What already exists:
- Compare tab with source/target connection pickers
- "Tables" mode toggle
- `TableCompareService.cs` — compares table STRUCTURES (columns/types), NOT row data
- Deploy infrastructure for schema objects

### What needs to be built:
- **DataCompareService.cs** — new service that:
  1. Detects the PK columns for a given table (query `sys.indexes` + `sys.index_columns`)
  2. Fetches all rows from source and target tables
  3. Matches rows by PK
  4. Compares field values and produces a list of `DataCompareRow` results with per-field status
- **DataCompareResult model** — row-level and field-level comparison results
- **UI in CompareView.axaml** — the master grid + detail panel layout, column filter/search
- **CompareViewModel additions** — data compare properties, commands, filtering logic
- **SQL generation** — INSERT/UPDATE/DELETE script generation for deploying row differences

### Performance consideration:
For large tables, loading ALL rows is expensive. Consider:
- Default limit: first 1000 rows (with a "Load All" button)
- Or: require a filter before comparing (e.g. must pick a column and value first)
- The column filter + search helps narrow down what you're looking at after load

### PK detection:
Use `sys.indexes` where `is_primary_key = 1` joined with `sys.index_columns` and `sys.columns` to auto-detect the PK. If no PK exists, prompt the user to select the matching column(s) manually.

---

## UI Layout

```
┌─────────────────────────────────────────────────────────┐
│ Source: [PROD ▾]  [Tables]  Target: [DEV ▾]             │  Compare toolbar
├─────────────────────────────────────────────────────────┤
│ Table: [Employees ▾]   Filter: [EmployeeId ▾] [1     ] │  Table selector + column filter
├─────────────────────────────────────────────────────────┤
│ 16 compared: 12 identical, 3 different, 1 source only   │  Summary bar
├───┬──────┬───────┬──────┬────────┬─────────┬───────────┤
│ ☐ │ Stat │ EmpId │ Name │ Email  │ Salary  │ Phone ... │  Master grid
│ ☐ │  ✓   │ 1     │ Sarah│ s@c.c  │ 185000  │ 555-0101  │
│ ☐ │  ≠   │ 2     │ James│ j@c.c  │ 145000  │ 555-0102  │  ← amber row
│ ☐ │  ←   │ 16    │ New  │ n@c.c  │ 50000   │ NULL      │  ← green (source only)
├───┴──────┴───────┴──────┴────────┴─────────┴───────────┤
│ Column        │ Source         │ Target        │ Status  │  Detail panel
│ EmployeeId    │ 2              │ 2             │ Match   │
│ FirstName     │ James          │ James         │ Match   │
│ LastName      │ Wilson         │ Wilson        │ Match   │
│ Salary        │ 145000.00      │ **155000.00** │ Diff    │  ← highlighted
│ Phone         │ 555-0102       │ NULL          │ Diff    │
├─────────────────────────────────────────────────────────┤
│           [Deploy Row] [Deploy Selected (2)] [Show SQL] │  Action bar
└─────────────────────────────────────────────────────────┘
```

---

## Priority

This is a high-value feature — it's the main thing SSMS lacks a good built-in tool for. Third-party data compare tools cost $300+. Building this into the app is a major selling point.

Build order:
1. DataCompareService (PK detection + row fetch + comparison logic)
2. Master grid UI with status indicators
3. Column filter + search
4. Detail panel with field-by-field view
5. Deploy/SQL generation (can reuse patterns from edit mode's Apply logic)
