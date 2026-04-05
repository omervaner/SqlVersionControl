# Tools Menu — Feature Spec

New "Tools" menu in the menu bar (between Edit and Help). Each tool opens as a dialog or panel.

---

## 1. Query Formatter


**What:** Format ugly SQL into clean, readable SQL.

**UI:** No dialog needed for the common case. Ctrl+Shift+F formats the selected text in the editor (or all text if nothing selected). For advanced options, add a Tools → Format SQL menu item that opens a dialog with formatting preferences.

**Library:** Use `PoorMansTSqlFormatterLib` NuGet package (MIT license, by TaoK). It's T-SQL specific, handles procedures/batches/GO, preserves comments, and is fault-tolerant. If it doesn't work on .NET 9 due to the .NET Framework 2.0 target, fall back to `Hogimn.Sql.Formatter` (v2.0.2, .NET Standard 2.0, use `Dialect.TSql`).

**Formatting rules (configure the library to match):**
- SELECT, FROM, WHERE, GROUP BY, ORDER BY, HAVING all start at the same indent level (left-aligned with each other)
- Column lists after SELECT indented one level
- JOIN clauses indented one level from FROM, ON indented under its JOIN
- Subqueries indented one additional level — each nested SELECT gets its own indent block
- CASE/WHEN/THEN/END indented properly
- Comments preserved in place — never moved or stripped
- Keywords uppercased (SELECT, FROM, WHERE, JOIN, etc.)
- Indent with spaces (4 spaces default, configurable)
- AND/OR at the start of the line, not end
- Commas before column names (leading commas), configurable

**Shortcut:** Ctrl+Shift+F (format selected text in editor, or format all if nothing selected)

**Menu item:** Tools → Format SQL (Ctrl+Shift+F)


---

## 2. Text Compare

**What:** Compare any two text blocks side by side with diff highlighting. Not SQL-specific — works for any text.

**UI:** Dialog with two text editors side by side (left and right). Paste or type into each. "Compare" button shows inline diff highlighting — same as the existing DiffView component used in Version History.

**Implementation:** Reuse the existing `DiffView` component and diff logic from Version History. The infrastructure is already there — this just exposes it as a standalone tool.

**Menu item:** Tools → Text Compare

---

## 3. SQL Quoter

**What:** Paste a list of values (one per line), get them formatted for SQL IN clauses.

**Input:**
```
P100
P200
   P300
```

**Output options:**
- String quotes: `'P100', 'P200', 'P300'` (default — for varchar/nvarchar)
- Numeric (no quotes): `100, 200, 300`
- Parenthesized: `('P100', 'P200', 'P300')` — ready to paste after IN
- N-prefixed: `N'P100', N'P200', N'P300'` — for Unicode strings

**UI:** Dialog with:
- Input text area (paste your list here)
- Output text area (formatted result, read-only, auto-updates as you type)
- Radio buttons or dropdown for output format (String / Numeric / Parenthesized / N-String)
- "Copy" button to copy output to clipboard
- Auto-trims whitespace from each line
- Skips empty lines

**Implementation:** Pure string manipulation, no SQL connection needed. Trivial to build.

**Shortcut:** No global shortcut, but could have Ctrl+C in the output area auto-copy.

**Menu item:** Tools → SQL Quoter

---



**What:** Analyze indexes on the current database — find unused indexes wasting write performance, missing indexes the optimizer is begging for, and duplicate/overlapping indexes.

**UI:** Dialog with three tabs, each with a DataGrid. Database dropdown at the top (defaults to current connection's database).

### Tab 1: Unused Indexes
Indexes with zero or near-zero reads but high write overhead — prime candidates for dropping.

**Query:** `sys.dm_db_index_usage_stats` joined with `sys.indexes`, `sys.objects`, `sys.schemas`

**Columns:**
- Schema.Table (e.g. `dbo.Orders`)
- Index Name
- Type (Clustered / Nonclustered / Unique)
- User Seeks (reads)
- User Scans (reads)
- User Lookups (reads)
- User Updates (writes)
- Total Reads (seeks + scans + lookups)
- Last Read Date
- Last Write Date
- Row Count (from `sys.dm_db_partition_stats`)
- Size MB (from `sys.dm_db_partition_stats` — `reserved_page_count * 8 / 1024`)

**Default sort:** Total Reads ascending — indexes with 0 reads at the top.

**Filters:**
- Checkbox: "Hide clustered indexes" (on by default — you almost never drop a clustered index)
- Checkbox: "Hide primary keys" (on by default)
- Min writes threshold (default 0 — show all)

**Actions:**
- Select one or more rows → "Generate DROP" button → generates `DROP INDEX [index] ON [schema].[table]` script, opens in new query tab
- "Refresh" button to re-query

**Important:** DMV stats reset on server restart. Show a note at the bottom: "Stats since last server restart: [date from `sys.dm_os_sys_info` `sqlserver_start_time`]"

### Tab 2: Missing Indexes
Indexes the query optimizer has suggested during query execution.

**Query:** `sys.dm_db_missing_index_details` + `sys.dm_db_missing_index_groups` + `sys.dm_db_missing_index_group_stats`

**Columns:**
- Schema.Table
- Equality Columns (columns in `=` predicates)
- Inequality Columns (columns in `>`, `<`, `BETWEEN`, etc.)
- Included Columns
- User Seeks (how many times the optimizer wanted this index)
- User Scans
- Avg User Impact (% improvement the optimizer estimates)
- Last Seek Date
- Score (calculated: `user_seeks * avg_total_user_cost * avg_user_impact / 100` — higher = more impactful)

**Default sort:** Score descending — highest impact suggestions first.

**Actions:**
- Select one or more rows → "Generate CREATE" button → generates `CREATE NONCLUSTERED INDEX [IX_table_columns] ON [schema].[table] ([equality_cols], [inequality_cols]) INCLUDE ([included_cols])` with a sensible auto-generated name, opens in new query tab
- "Refresh" button

### Tab 3: Duplicate / Overlapping Indexes
Indexes that are subsets of other indexes on the same table — one can likely be dropped.

**Query:** Compare `sys.index_columns` across indexes on the same table. Two indexes overlap if the key columns of one are a leading prefix of the other.

**Columns:**
- Schema.Table
- Index 1 Name — Key Columns — Include Columns
- Index 2 Name — Key Columns — Include Columns
- Relationship: "Exact Duplicate" or "Index 1 is subset of Index 2" or "Overlapping"

**Default sort:** Exact duplicates first, then subsets, then overlapping.

**Actions:**
- "Generate DROP" for the smaller/redundant index

### General
- All tabs share the database dropdown at the top
- Status bar showing "Stats since server restart: [datetime]"
- Export button on each tab (reuse existing export functionality)
- Respects dialog base styling (Section 14)

**Shortcut:** None (menu only)
**Menu item:** Tools → Index Analysis



## 6. Script Object As... (Object Explorer Right-Click)


**What:** Right-click any object in Object Explorer to generate common SQL scripts.

**NOT a Tools menu item** — this is an OE context menu addition.

**Right-click menu items for Tables:**
- Script as → SELECT TOP 100 (opens in new query tab)
- Script as → INSERT template (generates INSERT with all columns, placeholder values)
- Script as → CREATE (full CREATE TABLE script including constraints, indexes)
- Script as → DROP (with IF EXISTS safety)
- Script as → ALTER (for adding columns — template with ALTER TABLE ADD)

**Right-click menu items for Stored Procedures / Functions / Views:**
- Script as → CREATE (current definition from sys.sql_modules)
- Script as → ALTER (same definition but with ALTER instead of CREATE)
- Script as → DROP (with IF EXISTS)
- Script as → EXEC template (for sprocs — generates EXEC with parameter placeholders)

**Right-click menu items for Columns:**
- Copy column name
- Script as → SELECT with this column
- Script as → WHERE clause (column = '')

**Implementation:** 
- For CREATE scripts: query `sys.sql_modules` for code objects, `INFORMATION_SCHEMA.COLUMNS` + `sys.indexes` for tables
- For SELECT/INSERT templates: generate from column metadata already loaded in OE
- Scripts open in a new query tab (same as double-clicking a table already does)

---

## 7. Quick Quote Button (Editor Toolbar)

**What:** A `"` button in the editor toolbar (next to the lightning bolt icon) that instantly quotes the selected text in the editor. No dialog needed.

**How it works:**
1. Paste a list of values into the editor (one per line, or space/tab separated)
2. Highlight the text
3. Click the `"` button (or press a shortcut like Ctrl+Shift+Q)
4. The selected text is replaced in-place with quoted, comma-separated values

**Example:**
```
-- Select this:
P100
P200
   P300

-- Click " button, becomes:
'P100', 'P200', 'P300'
```

**Behavior:**
- Trims whitespace from each line
- Skips empty lines
- Default: single-quoted strings with comma separation (most common SQL use case)
- If ALL values are numeric (no letters), output without quotes: `100, 200, 300`
- Hold Shift+click (or use a secondary shortcut) for N-prefixed: `N'P100', N'P200', N'P300'`

**UI:** Small toolbar button next to the lightning bolt (autocomplete) and clock (history) icons. Icon: `"` character or a quotation mark SVG. Transparent background until hover, same style as the other toolbar icons.

**Tooltip:** `Quote selection (Ctrl+Shift+Q)`

**This is the quick version.** The full SQL Quoter dialog (Section 3) still exists for when you need more control (parenthesized output, copy to clipboard without replacing editor text, etc.). This toolbar button is the fast path for the most common case.

---



## 8. Redo Keybinding Fix

**What:** Redo (Cmd+Shift+Z on Mac, Ctrl+Y on Windows) doesn't work. Undo works fine — AvaloniaEdit has built-in undo/redo support, but the redo keybinding isn't mapped.

**Fix:** Add key bindings for redo in the editor. AvaloniaEdit's TextArea has an `UndoStack` — redo is just `UndoStack.Redo()`. Wire up:
- **Mac:** Cmd+Shift+Z
- **Windows:** Ctrl+Y (and optionally Ctrl+Shift+Z for cross-platform consistency)

Check if there's a conflicting binding eating the keystroke. If AvaloniaEdit already registers these internally, something in our key handling might be intercepting them.

**Scope:** Small fix — likely just adding/unblocking keybindings in the editor view.

---



## 9. Peek Definition (Cmd+Click / Ctrl+Click)

**What:** Cmd+Click (Mac) or Ctrl+Click (Windows) on a stored procedure, function, or view name in the editor → loads its definition in the results panel below the editor.

**Why:** Mouse-free workflow. You're writing a query, you reference `usp_GetStock`, you Cmd+Click it and instantly see the source code without leaving your editor. This is the sp_helptext equivalent — but faster and integrated.

**How it works:**
1. User Cmd+Clicks (or Ctrl+Clicks) a word in the editor
2. App extracts the word under cursor
3. Runs the equivalent of `sp_helptext 'word'` or queries `sys.sql_modules` joined with `sys.objects` for the definition
4. Displays the result in the results panel as a read-only SQL tab (with syntax highlighting)
5. If the object doesn't exist or isn't a scriptable type, show a brief "Object not found" message in the results panel

**Scope:** Stored procedures, functions, views, triggers. Tables don't have "definitions" — those go through Script Object As.

**Key detail:** The results panel shows the definition with syntax highlighting, not as a plain text grid. Ideally a read-only AvaloniaEdit instance in the results area.

---

## 10. Context Menu Styling

**What:** The OE right-click context menu font size is too large compared to the rest of the app. It looks like the system default rather than matching CheatTeam's design system.

**Fix:** Apply the app's font size and styling to the context menu. Should match the 12px (or whatever the design system specifies) monospace or UI font, consistent padding, consistent with the rest of the UI. Same dark theme background/foreground as the app.

---



## 11. Highlight All Occurrences of Selected Word

**What:** Select a word (or double-click to select it) in the editor → every other occurrence of that word gets a subtle background highlight throughout the document.

**Why:** You're looking at a big query and want to see everywhere `EmployeeId` is used — in the SELECT, the JOIN, the WHERE. Select it once, instantly see all 8 occurrences lit up. Indispensable for reading unfamiliar SQL.

**Behavior:**
- Triggered on text selection (not just double-click — any selection of a complete word)
- Case-insensitive matching (SQL is case-insensitive)
- Whole-word matching only (selecting `Id` shouldn't highlight `EmployeeId`)
- Subtle background highlight — not the same as Find results. Something like a soft yellow/amber in dark theme, soft tan in light theme
- Clears when selection changes or cursor moves to a non-matching position
- No UI controls needed — purely automatic

**Implementation hint:** AvaloniaEdit has `TextArea.TextView.LineTransformers` for custom rendering. Add a `DocumentColorizingTransformer` that checks for the current selected word and applies a background color to matches.

---

## 12. Move Line Up/Down (Alt+Up / Alt+Down)

**What:** Alt+Up moves the current line (or selected lines) up one position. Alt+Down moves them down.

**Why:** Reordering columns in a SELECT, shuffling WHERE conditions, moving JOIN clauses around — all without cut-and-paste.

**Behavior:**
- Single line: moves the line the cursor is on
- Multi-line selection: moves the entire selected block
- Maintains cursor position within the line
- Works at document boundaries (can't move line 1 up, can't move last line down)

---

## 13. Go to Line (Cmd+G / Ctrl+G)

**What:** Hit Cmd+G (Mac) or Ctrl+G (Windows) → small input popup appears → type a line number → editor scrolls to and highlights that line.

**Why:** Error messages say "error on line 47". You want to jump there instantly instead of scrolling or mentally counting.

**Behavior:**
- Lightweight popup (not a full dialog) — similar to VS Code's go-to-line bar at the top of the editor
- Type a number, hit Enter, popup closes, cursor moves to that line
- Escape dismisses without moving
- Invalid input (non-numeric, out of range) is silently ignored or shows brief feedback
- Line is scrolled to center of viewport, not just barely visible

---



## 14. Dialog Base Styling

**What:** All dialogs (Settings, About, Connection, Save Query, Open Query, Close Tab, Deploy, Rollback, and any future dialogs like Query Formatter) look inconsistent with the main app. They use system-default backgrounds, mismatched button styles, and wrong font sizes.

**Fix:** Create a single reusable dialog style that all dialogs inherit. One fix, every dialog benefits — including future ones.

**Requirements:**
- **Background**: Must use the app's chrome/panel color from the design system — NOT system default gray, NOT pure black. Matches `{DynamicResource PanelHeaderBackground}` or equivalent in both dark and light themes
- **Buttons**: 2px border radius, proper padding (8px 16px), use `ButtonPrimary` for the main action (Save/OK), `ButtonSecondary` for Cancel/Close. Consistent height and spacing
- **Font size**: Match the app's UI font size (not monospace — the UI font). Currently the Settings dialog labels feel larger than the rest of the app
- **Input fields**: Styled consistently — same background, border color, corner radius, padding as inputs elsewhere in the app
- **Section headers**: Consistent color and weight (the blue "Appearance" / "Version History" headers in Settings are fine as a pattern — just make sure all dialogs use the same approach)
- **Spacing**: Consistent margins between sections, between label and input, between buttons
- **Title bar**: Dialog title centered, matching the app's chrome color

**Implementation:** Ideally a set of shared styles in a `DialogStyles.axaml` resource dictionary that all dialog AXAML files reference. Or add the styles to the existing AppTheme files. The key is ONE definition, all dialogs consume it.

**Dialogs to update:** Settings, About, Connection, Quick Connection, Save Query, Open Query, Close Tab, Deploy, Rollback.

---


## Implementation Priority

1. ~~Redo Keybinding Fix~~ ✅ DONE
2. ~~Context Menu Styling~~ ✅ DONE
3. ~~SQL Quoter~~ ✅ DONE
4. ~~Quick Quote Button~~ ✅ DONE
5. ~~Highlight All Occurrences~~ ✅ DONE
6. ~~Move Line Up/Down~~ ✅ DONE
7. ~~Go to Line~~ ✅ DONE
8. ~~Script Object As~~ ✅ DONE
9. ~~Peek Definition~~ ✅ DONE
10. ~~Dialog Base Styling~~ ✅ DONE
11. ~~Query Formatter~~ ✅ DONE
12. ~~Text Compare~~ ✅ DONE
13. **Index Analysis** — three-tab dialog: unused indexes, missing indexes, duplicate/overlapping indexes


## Menu Structure

```
File  Edit  Tools  Help  |  Editor  History  Compare  Exec Plan  Settings
              │
              ├── Format SQL          Ctrl+Shift+F
              ├── SQL Quoter
              ├── Text Compare
              ├──────────────
              ├── Object Dependencies
              ├── Index Analysis
```
