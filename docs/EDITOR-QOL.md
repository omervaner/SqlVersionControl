# Editor QoL Improvements

Small, high-impact editor enhancements. Each is independent — implement in any order.

---

## 1. Comment / Uncomment

**What:** Select one or more lines, hit the shortcut, all selected lines get `--` prefixed. Hit the other shortcut to remove the `--` prefix.

**Shortcuts:**
- **Comment:** Cmd+K (Mac) / Ctrl+K (Windows)
- **Uncomment:** Cmd+L (Mac) / Ctrl+L (Windows)

**Behavior:**
- Comment: prepends `-- ` (with a space) to each selected line. If no selection, comments the current line.
- Uncomment: removes leading `-- ` or `--` from each selected line. Handles both with and without the trailing space.
- Works on partial selections — if the selection starts mid-line, still operates on the full lines covered by the selection.
- Cursor/selection is preserved after the operation.

**Why not a toggle?** Two separate shortcuts are simpler to implement and reason about. No ambiguity when a selection has a mix of commented and uncommented lines.

---

## 2. Uppercase / Lowercase Selection

**What:** Select text, hit the shortcut, selection is converted to uppercase or lowercase.

**Shortcuts:**
- **Uppercase:** Cmd+Shift+U (Mac) / Ctrl+Shift+U (Windows)
- **Lowercase:** Cmd+Shift+L (Mac) / Ctrl+Shift+L (Windows)

**Behavior:**
- Operates on the selected text only. If no selection, does nothing (don't uppercase the whole document by accident).
- Preserves the selection after the operation so you can immediately see the result and undo if needed.
- Uses .NET's `ToUpperInvariant()` / `ToLowerInvariant()` to handle Turkish characters correctly (ı→I, i→İ, etc.). **Important:** Turkish locale has special casing rules — `i`.ToUpper() should give `İ` not `I` in Turkish context. Test with Turkish characters.

---

## 3. Copy Results with Column Headers

**What:** Copy selected rows from the results grid with column headers included in the first row.

**Shortcut:** Cmd+Shift+C (Mac) / Ctrl+Shift+C (Windows) when focus is in the results grid.

**Behavior:**
- Copies as tab-separated text (TSV) — pastes cleanly into Excel, Google Sheets, Slack, etc.
- First row is column names, subsequent rows are data.
- If no rows are selected, copies all visible rows.
- Regular Cmd+C / Ctrl+C continues to work as before (data only, no headers).
- Right-click context menu on results grid also gets a "Copy with Headers" option.

**Example output:**
```
EmployeeId	FirstName	LastName	Email
1	Sarah	Chen	sarah.chen@company.com
2	James	Wilson	james.wilson@company.com
```

---

## 4. Pin Result Tab

**What:** Pin a result tab so the next query execution creates a new result tab instead of replacing the pinned one.

**UI:** Small pin icon on the result tab header (next to the tab label). Click to toggle pin state. Pinned tabs get a subtle visual indicator (the pin icon stays filled, or the tab gets a small dot).

**No shortcut** — pin icon click only.

**Behavior:**
- Unpinned result tabs are replaced on the next F5 (current behavior).
- Pinned result tabs are preserved. A new result tab is created for the new query results.
- Pinned tabs can be manually closed with the × button.
- Tab label for pinned tabs could show the query snippet or timestamp so you know what it was: "Result 1 (pinned)" or "Result 1 - 14:32".
- Right-click a pinned result tab → "Open Source Query" → opens the original query in a new query tab. The query text is stored as metadata when the result is pinned.
- This also works for unpinned result tabs — right-click any result tab to recover the query that produced it.


**Use case:** Run a query, pin the result, modify the query, run again — now you can visually compare before and after side by side in the results area.

---

## 5. Word Wrap Toggle

**What:** Toggle word wrap in the SQL editor for long lines.

**Shortcut:** Option+Z (Mac) / Alt+Z (Windows) — same as VS Code.

**Behavior:**
- Toggles between wrap and no-wrap in the active editor tab.
- State is per-tab (one tab can be wrapped, another not).
- Line numbers still refer to actual lines, not visual wrapped lines.
- Default: no wrap (current behavior).
- Menu item: Edit → Toggle Word Wrap (Option+Z)

**Implementation:** AvaloniaEdit has a `WordWrap` property on `TextEditor`. This is just toggling that property.

---

## Implementation Priority

1. **Comment / Uncomment** — most frequently used, biggest daily impact
2. **Copy with Headers** — constant need when sharing query results
3. **Uppercase / Lowercase** — quick win, simple string operation
4. **Word Wrap Toggle** — one property toggle, trivial
5. **Pin Result Tab** — most complex, but great for comparing results
