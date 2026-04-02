# MISTAKES — Bugs & UX Issues Found During Code Audit (April 2026)

Each item is independent and can be fixed in any order. Priority ranking: 🔴 = real bug, 🟡 = UX issue, ⚪ = minor/cleanup.

---

## ✅ ~~1. Shared `DatabaseService._connectionString` mutation — wrong-server risk~~

**Files:** `Services/DatabaseService.cs`, `ViewModels/MainWindowViewModel.cs`

`MainWindowViewModel.SetHistoryConnection()` calls `_db.SetConnection(settings)` on the **same** `DatabaseService` instance shared with the query editor. This mutates the single `_connectionString` field.

Any query tab that has a null `TabConnectionString` (fallback path) will silently execute against whatever server the History/Activity view last connected to:

```csharp
// QueryTabViewModel.RunQueryAsync()
var execResult = TabConnectionString != null
    ? await _db.ExecuteQueryAsync(TabConnectionString, SelectedDatabase!, sql, _cts.Token)
    : await _db.ExecuteQueryAsync(SelectedDatabase!, sql, _cts.Token);
//    ^^^^^^^^ This branch uses _db._connectionString — which History view may have changed
```

Right now tabs usually get `TabConnectionString` set via `AddNewTab()`, so this is latent. But the moment a tab doesn't have one (session restore failure, edge case in tab creation), queries silently go to the wrong server.

**Fix:** Either give History/Activity views their own `DatabaseService` instances, or remove the shared mutable `_connectionString` pattern entirely and require explicit connection strings everywhere.

---

## ✅ ~~2. `RunQueryAsync()` doesn't clear `TraceEvents` — stale trace tab~~

**Files:** `ViewModels/QueryTabViewModel.cs`

After running a trace query (`RunWithTraceAsync`), then running a normal query (`RunQueryAsync`), the `TraceEvents` collection is never cleared. `RunWithTraceAsync` correctly calls `TraceEvents.Clear()` at the top, but `RunQueryAsync` doesn't.

This means `RebuildResultTabs()` still sees `_viewModel.TraceEvents.Count > 0` and shows the Trace tab with stale events from the previous trace run, making it look like the new query generated those trace events.

**Fix:** Add `TraceEvents.Clear()` at the top of `RunQueryAsync()`, right after `Results.Clear()` and `Messages = []`.

---

## ✅ ~~3. `BuildColumnsForGrid` doesn't apply NullDisplayConverter~~

**Files:** `Views/QueryTabView.Results.cs`

The "single source of truth" method for building grid columns creates bindings without any converter:

```csharp
grid.Columns.Add(new DataGridTextColumn
{
    Header = result.ColumnNames[i],
    Binding = new Binding($"[{i}]", BindingMode.TwoWay),
    IsReadOnly = true,
    // No converter — NULLs render as empty strings
});
```

`_nullTextConverter` (NullDisplayConverter) and `GetNullForeground()` are defined in the class but not wired into column bindings. NULL values show as empty cells instead of styled "NULL" text.

If `OnDataGridLoadingRow` handles null styling at the row level instead, that causes flicker during fast scrolling — null cells briefly appear empty before the row-loading event fires.

**This exact issue is documented in CLAUDE.md as a past source of painful bugs** (DataGrid column building copy-pasted into three places, only one had the converter).

**Fix:** Add the NullDisplayConverter to the column binding in `BuildColumnsForGrid`, or verify `OnDataGridLoadingRow` handles it and document that as the intentional approach.

---

## 🟡 4. `SessionService.AddQueryToHistory()` writes entire session to disk on every F5 — POSTPONED, NEEDS FURTHER DISCUSSION

**Files:** `Services/SessionService.cs`

Every single query execution calls `AddQueryToHistory()` → `Save()` which does synchronous `File.WriteAllText` of the entire session (all tabs + up to 200 history entries). Combined with the 30-second autosave timer in `QueryEditorHost` also calling `SaveSession()`, rapid query iteration hammers the disk.

**Fix:** Either debounce the history save (batch writes every N seconds), make the write async, or separate query history into its own file so it's a smaller write. At minimum, `AddQueryToHistory` should just update the in-memory list and let the autosave timer handle persistence.

---

## ✅ ~~5. "Close Others" / "Close All" doesn't abort on Cancel~~

**Files:** `Views/QueryEditorHost.Tabs.cs`

When closing multiple tabs via the context menu, each tab is closed sequentially with `await CloseTabAsync(idx)`. If one tab has unsaved changes and the user clicks Cancel, that tab's close is aborted — but the loop continues to the next tab.

```csharp
var tabsToClose = _tabs.Where(t => t != targetTab).ToList();
foreach (var tab in tabsToClose)
{
    var idx = _tabs.IndexOf(tab);
    if (idx >= 0) await CloseTabAsync(idx);
    // ← Should check if user cancelled and break
}
```

Users expect Cancel to abort the entire batch operation, not skip one tab and keep closing the rest.

**Fix:** Have `CloseTabAsync` return a bool indicating whether the close was cancelled, and break out of the loop if so.

---

## ✅ ~~6. Edit mode type conversion failures are silent~~

**Files:** `Models/EditableRow.cs`

`ConvertValue()` catches all exceptions and returns the raw string:

```csharp
catch
{
    return text; // Keep as string if conversion fails
}
```

User types "abc" into an INT column → no red border, no validation warning, nothing. The error only surfaces when they hit Apply and the database throws a type error inside a transaction. The error message is then the raw SQL Server error, not a user-friendly "invalid integer" message.

**Fix:** Either validate on cell edit end and show inline validation errors (red border + tooltip), or at minimum validate before Apply and show a clear summary of which cells have type mismatches.

---

## ⚪ 7. `SplitOnGo` doesn't handle GO inside string literals or comments — WONTFIX (matches SSMS behavior)

**Files:** `Services/DatabaseService.cs`

The GO splitter does a line-level regex check (`^GO\s*$`). A multiline string literal or block comment containing GO on its own line will incorrectly split the batch:

```sql
/*
GO
*/
SELECT 1
```

This splits into two batches: `/*` (unclosed comment) and `*/\nSELECT 1` (syntax error).

SSMS has the same limitation, so this matches user expectations in most cases. But it's worth noting for users migrating complex scripts.

**Fix:** Implement a state machine that tracks whether we're inside a string literal (`'...'`) or block comment (`/*...*/`) and only splits on GO lines that are outside both. Low priority since SSMS doesn't do this either.

---

## ✅ ~~8. Connection timeout hardcoded at 5 seconds everywhere~~

**Files:** `Models/ConnectionSettings.cs`, `Services/ConnectionRegistry.cs`, `ViewModels/ConnectionManagerViewModel.cs`

All three places hardcode `ConnectTimeout = 5`. On a VPN or high-latency link (e.g., connecting to the Adana warehouse), this causes false connection failures.

**Fix:** Add a `ConnectionTimeout` property to `AppSettings` with a default of 5, expose it in the Settings dialog, and use it in all three locations instead of the hardcoded value.

---

## ⚪ 9. `OnSearchTextChanged` code search can flash stale results — WONTFIX (race window is academic, cancellation checks sufficient)

**Files:** `ViewModels/MainWindowViewModel.cs`

The debounced code search runs on `Task.Run` and marshals results back via `Dispatcher.UIThread.InvokeAsync`. There's a narrow race:

1. User types "foo" → `FilterObjects()` clears Objects, debounce starts search for "foo"
2. User types "foobar" → `FilterObjects()` clears Objects again, debounce cancels "foo" search, starts "foobar"
3. The "foo" search's `InvokeAsync` was already queued on the UI thread before cancellation
4. The "foo" invoke runs, checks `token.IsCancellationRequested` (should catch it), but if the invoke was already executing when Cancel was called, stale "foo" results get appended to the "foobar" filtered list

The `IsCancellationRequested` check inside the invoke mitigates this in most cases, but it's not airtight.

**Fix:** Add a generation counter — increment on each `OnSearchTextChanged`, capture the value before `Task.Run`, and discard results if the counter has advanced by the time the UI invoke runs.

---

## ✅ ~~10. `IntellisenseService.Tail()` is dead code~~

**Files:** `Services/IntellisenseService.cs`

```csharp
private static string Tail(string s, int n) =>
    s.Length <= n ? s.ReplaceLineEndings(" ") : s[^n..].ReplaceLineEndings(" ");
```

Defined but never called anywhere. Minor cleanup.

**Fix:** Delete it.
