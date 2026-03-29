# Tools Menu — Feature Spec

New "Tools" menu in the menu bar (between Edit and Help). Each tool opens as a dialog or panel.

---

## 1. Query Formatter

**What:** Paste ugly SQL, get clean formatted SQL.

**UI:** Dialog with two panels — raw SQL on the left, formatted output on the right. "Format" button in the middle. Options: indent style (spaces/tabs), indent width (2/4), uppercase keywords (yes/no), comma position (before/after).

**Implementation:** Use an open-source SQL formatting library for .NET. Options:
- `TSqlFormatter` / `PoorMansTSqlFormatterLib` (MIT, available on NuGet)
- Or build a basic formatter: uppercase keywords, indent after BEGIN/SELECT/FROM/WHERE/JOIN, newline before major clauses

**Shortcut:** Ctrl+Shift+F (format selected text in editor, or format all if nothing selected). This should also work directly in the query editor — no need to open the dialog for quick formatting.

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

## 4. Object Dependencies

**What:** "What references this table/column?" — find every sproc, view, function, and trigger that references a given object.

**UI:** Dialog or panel with:
- Object picker (dropdown or search, populated from OE)
- Results grid showing: referencing object name, object type, schema, and the line(s) where the reference appears
- Click a result to see the object's definition with the reference highlighted

**Implementation:** Query `sys.dm_sql_referencing_entities(@object_name, 'OBJECT')` for objects that reference the selected object. Optionally also search `sys.sql_modules` for text-based matches (catches dynamic SQL references that the DMV misses).

**Can also be triggered from:** Object Explorer right-click → "Find Dependencies" (see Section 6 below)

**Menu item:** Tools → Object Dependencies

---

## 5. Index Analysis

**What:** Show unused indexes (wasting write performance) and missing indexes (suggested by the query optimizer).

**UI:** Dialog or panel with two tabs:

**Unused Indexes tab:**
- Query `sys.dm_db_index_usage_stats` joined with `sys.indexes` and `sys.objects`
- Show: table name, index name, type (clustered/nonclustered), reads (seeks + scans + lookups), writes (updates), last read date, last write date
- Sort by reads ascending — indexes with 0 reads and high writes are prime candidates for dropping
- "Generate DROP" button: creates DROP INDEX script for selected indexes

**Missing Indexes tab:**
- Query `sys.dm_db_missing_index_details` + `sys.dm_db_missing_index_groups` + `sys.dm_db_missing_index_group_stats`
- Show: table name, equality columns, inequality columns, included columns, user seeks, user scans, avg user impact (%), last seek date
- Sort by impact descending — highest impact suggestions first
- "Generate CREATE" button: creates CREATE INDEX script for selected suggestions

**Implementation:** Standard DMV queries, no special permissions needed (just VIEW SERVER STATE). Results displayed in DataGrids.

**Menu item:** Tools → Index Analysis

---

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


## Implementation Priority

1. **Redo Keybinding Fix** — 5 minute fix, critical missing functionality ✅ DONE
2. **Context Menu Styling** — quick fix, makes the right-click menu match the app
3. **SQL Quoter** — trivial to build, high daily-use value ✅ DONE
4. **Quick Quote Button** — string manipulation on editor selection ✅ DONE
5. **Highlight All Occurrences** — automatic, no UI, huge readability win
6. **Move Line Up/Down** — quick keybinding, daily-use editor shortcut
7. **Go to Line** — small popup, pairs with error messages
8. **Script Object As...** — medium effort, huge usability win, makes OE actually useful beyond browsing
9. **Peek Definition** — Cmd+Click on proc names, medium effort, huge workflow win
10. **Query Formatter** — medium effort (if using a NuGet library), daily-use feature
11. **Object Dependencies** — medium effort, important for schema changes
12. **Index Analysis** — medium effort, high value for DBAs
13. **Text Compare** — low effort (reuses DiffView), nice to have


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
