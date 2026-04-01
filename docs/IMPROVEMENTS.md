# Lookout — UX Improvements

Date: March 30, 2026  
Source: Full codebase UX audit

All items below are confirmed changes to implement. Item 14 (autosave dirty flag) is the only item deferred for discussion.

---

## Confirmed Changes

### 1. Edit mode: confirm before discarding pending row changes on F5

**File:** `QueryTabViewModel.cs` → `RunQueryAsync()`

Currently `if (IsEditMode) ExitEditMode();` silently discards all pending edits when the user hits F5. If `PendingChangeCount > 0`, show a confirmation dialog: "You have X unsaved row changes. Discard and run query?"

The ExitEditMode call needs to become async so it can await the dialog. Add a `ConfirmDiscardEditsAsync()` method that returns bool, called before ExitEditMode.

---

### 2. Quick-switch buttons: don't create new tabs blindly

**File:** `MainWindow.axaml.cs` → `OnQuickSwitchClickedAsync()`

Currently every click on a quick-switch button calls `host.AddNewTab(connStr, conn)`, leading to tab proliferation. Change to:

- If active tab has no SQL text and no unsaved changes → switch the current tab's connection instead of creating a new one.
- Otherwise → create a new tab (current behavior).

This requires a new method like `QueryEditorHost.SwitchActiveTabConnection(string connStr, SavedConnection profile)` that updates the VM's `TabConnectionString`, `TabConnectionProfile`, reloads databases, and calls `UpdateStatusBar()`.

---

### 3. Compare tab: don't silently fall back to Windows Auth

**File:** `CompareViewModel.cs` → `BuildConnectionString()`

The line `settings.UseWindowsAuth = true; // No password — fall back to Windows auth` must go. Instead, return an empty string or null, and let the caller handle it by prompting for a password. The current behavior produces confusing "Login failed" errors.

Replace with:
```csharp
if (string.IsNullOrEmpty(password))
    return ""; // Caller checks for empty and prompts
```

And in `ConnectSourceAsync`/`ConnectTargetAsync`, check for empty connection string and request password.

---

### 4. Fix "Close Others" / "Close Right" index-shifting bug

**File:** `QueryEditorHost.axaml.cs` → `BuildTabContextMenu()`

The closure captures `tabIndex` at build time. During the async close loop, if the user cancels one prompt, subsequent indices are wrong. Fix:

```csharp
closeOthers.Click += async (_, _) =>
{
    // Collect tabs to close by reference, not index
    var tabsToClose = _tabs.Where((_, i) => i != tabIndex).ToList();
    foreach (var tab in tabsToClose)
    {
        var idx = _tabs.IndexOf(tab);
        if (idx >= 0) await CloseTabAsync(idx);
    }
};
```

Same pattern for Close Right and Close All.

---

### 5. Fix CaretPositionChanged handler leak on tab switch

**File:** `QueryEditorHost.axaml.cs` → `SyncToolbarWithActiveTab()`

Every tab switch adds a new `PositionChanged` handler without removing the old one. Over time this accumulates hundreds of stale handlers.

Fix: Store the handler reference and previous editor, unsubscribe before subscribing:

```csharp
private TextEditor? _lastCaretEditor;
private EventHandler? _caretHandler;

private void SyncToolbarWithActiveTab()
{
    // ... existing code ...

    // Unsubscribe previous caret handler
    if (_lastCaretEditor != null && _caretHandler != null)
        _lastCaretEditor.TextArea.Caret.PositionChanged -= _caretHandler;

    var activeTab = _tabs[_activeTabIndex];
    _lastCaretEditor = activeTab.Editor;
    _caretHandler = (_, _) =>
    {
        var line = activeTab.Editor.TextArea.Caret.Line;
        var col = activeTab.Editor.TextArea.Caret.Column;
        CaretPositionChanged?.Invoke(line, col);
    };
    activeTab.Editor.TextArea.Caret.PositionChanged += _caretHandler;
    // Fire immediately
    CaretPositionChanged?.Invoke(
        activeTab.Editor.TextArea.Caret.Line,
        activeTab.Editor.TextArea.Caret.Column);
}
```

---

### 6. Fix zoom range inconsistency

**Files:** `MainWindow.axaml.cs` — keyboard handler uses `Math.Min(..., 24)`, menu uses 32, mouse wheel uses 32.

Define a single constant and use it everywhere:

```csharp
private const int MinFontSize = 8;
private const int MaxFontSize = 32;
```

The keyboard handler's `24` limit at line `var newSize = Math.Min(_settings.Settings.FontSize + 1, 24);` should become `MaxFontSize`.

---

### 7. Compare tab: don't auto-connect on dropdown selection

**File:** `CompareViewModel.cs` → `OnSelectedSourceConnectionChanged` / `OnSelectedTargetConnectionChanged`

Currently selecting a connection in the dropdown immediately fires `ConnectSourceAsync()` / `ConnectTargetAsync()`, potentially triggering a password prompt. This is jarring if the user is just browsing.

Two options (pick one):
- **Option A:** Remove auto-connect from `OnSelected*Changed`. Add explicit "Connect" buttons next to each dropdown. Refresh button already calls `ConnectSourceAsync` / `ConnectTargetAsync` for unconnected selections.
- **Option B:** Only auto-connect if the connection has credentials available (Windows Auth or password in store). If it needs a password prompt, don't auto-connect — show a "Click to connect" status instead.

Option B is less disruptive and preserves the current seamless flow for connections that don't need a password.

---

### 8. Compare tab: disambiguate "Both" vs "Identical"

**File:** `CompareViewModel.cs` → `GetCompareStatus()`

Currently returns `"Both"` for uncompared objects that exist in both databases. The icon is `"="` which looks identical to the scanned `"Identical"` status.

Change:
```csharp
// Before scan
"Both" → "Uncompared" with icon "?"

// After scan
"Identical" stays "Identical" with icon "="
```

Update `CompareObject.StatusIcon`:
```csharp
"Uncompared" or "Both" => "?",
```

---

### 9. Batch deploy: report which objects failed and why

**File:** `CompareViewModel.cs` → `DeploySelectedAsync()`

Currently catches exceptions and only increments `failCount`. Change to collect errors:

```csharp
var failures = new List<(string ObjectName, string Error)>();

// In the catch block:
catch (Exception ex)
{
    failures.Add((obj.ObjectName, ex.Message));
    failCount++;
}

// In the status message:
if (failCount > 0)
{
    var failDetails = string.Join("; ", failures.Select(f => $"{f.ObjectName}: {f.Error}"));
    StatusMessage = $"Deployed {successCount}, {failCount} failed — {failDetails}";
}
```

For many failures, truncate to first 3 and add "(+N more)" so the status bar doesn't overflow.

---

### 10. ToggleTrigger: add confirmation before executing DDL

**File:** `ObjectExplorerViewModel.cs` → `ToggleTrigger()`

Currently calls `InsertTextRequested?.Invoke(sql, true)` which auto-runs the DDL. Every other destructive OE action has confirmation. Change `autoRun` to `false`:

```csharp
InsertTextRequested?.Invoke(sql, false); // Opens in new tab for user to review
```

This keeps the pattern consistent — the user sees the SQL and hits F5 to confirm.

---

### 11. Connection Manager password dialog: add keyboard support and styling

**File:** `ConnectionManagerDialog.axaml.cs` → password prompt lambda

The inline password dialog is a raw Window with no Enter/Escape handling. Add:

```csharp
dialog.KeyDown += (_, args) =>
{
    if (args.Key == Avalonia.Input.Key.Enter)
    {
        result = passwordBox.Text;
        dialog.Close();
    }
    else if (args.Key == Avalonia.Input.Key.Escape)
    {
        dialog.Close(); // result stays null
    }
};
```

Also auto-focus the password field on open:

```csharp
dialog.Opened += (_, _) => passwordBox.Focus();
```

Consider replacing this inline dialog with the existing `PasswordDialog.axaml` if one exists (there is a `PasswordDialog.axaml` in Views/).

---

### 12. Session restore: fix database selection race condition

**File:** `QueryEditorHost.axaml.cs` → `RestoreSession()`

Currently sets `vm.SelectedDatabase = tabState.SelectedDatabase` before `LoadDatabasesForTabAsync` completes. The database might not be in the list yet.

Fix: Pass the desired database to `LoadDatabasesForTabAsync` and apply it after loading:

```csharp
private async Task LoadDatabasesForTabAsync(
    QueryTabViewModel vm, string connectionString, string? selectDatabase = null)
{
    // ... existing fetch logic ...
    vm.SetDatabases(dbs, selectDatabase ?? vm.SelectedDatabase);
}
```

In RestoreSession:
```csharp
_ = LoadDatabasesForTabAsync(vm, vm.TabConnectionString, tabState.SelectedDatabase);
// Remove the separate vm.SelectedDatabase = ... line
```

---

### 13. Status bar: update on database change and reconnect within active tab

**File:** `MainWindow.axaml.cs` → `OnActiveTabPropertyChanged()`

Currently only handles `IsRunning`, `SelectedDatabase` (combo sync only), and `Databases`. Two gaps:

**Gap A — Database change doesn't update window title:**
Add `UpdateStatusBar()` call when `SelectedDatabase` changes:
```csharp
else if (e.PropertyName == nameof(QueryTabViewModel.SelectedDatabase))
{
    if (ToolbarDatabaseCombo.SelectedItem as string != vm.SelectedDatabase)
        ToolbarDatabaseCombo.SelectedItem = vm.SelectedDatabase;
    UpdateStatusBar(); // ← ADD THIS: updates window title with new DB name
}
```

**Gap B — Reconnect doesn't update status bar:**
Add handling for `TabConnectionString` changes:
```csharp
else if (e.PropertyName == nameof(QueryTabViewModel.TabConnectionString) ||
         e.PropertyName == nameof(QueryTabViewModel.TabConnectionProfile))
{
    UpdateStatusBar(); // Connection changed (e.g. via reconnect callback)
}
```

Note: `TabConnectionString` is currently a plain property, not `[ObservableProperty]`, so it doesn't fire `PropertyChanged`. Either make it observable:
```csharp
[ObservableProperty] private string? _tabConnectionString;
```
Or fire manually after assignment in `ReconnectCallback`.

---

### 15. Surface actual SQL error messages in connection failures

**Files:** `CompareViewModel.cs` → `ConnectSourceAsync` / `ConnectTargetAsync`, `ConnectionViewModel.cs` → `ConnectAsync()`

Currently connection failures show generic messages like "Connection failed" or "Could not connect to server." The actual `SqlException.Message` (which says things like "Cannot open database 'X'" or "Login failed for user 'Y'" or "A network-related error") is caught and thrown away.

Fix: Surface the exception message. In `CompareViewModel`:
```csharp
catch (Exception ex)
{
    IsSourceConnected = false;
    SourceStatus = $"Failed: {ex.Message}";
}
```

In `ConnectionViewModel.ConnectAsync`, replace:
```csharp
ErrorMessage = "Could not connect to server. Check your credentials.";
```
With:
```csharp
ErrorMessage = $"Connection failed: {_db.LastError ?? "Check your credentials."}";
```
Or catch the exception from `TestConnectionAsync` and surface it directly. The `TestConnectionAsync` method currently returns `bool` — consider changing to return `(bool success, string? error)`.

---

### 16. Crash report: dedicated viewer instead of SQL comment block

**File:** `MainWindow.axaml.cs` → `OnCrashViewClicked()`

Currently opens the crash report as SQL comments `/* ... */` in a query tab. Stack traces are hard to read inside SQL comments.

Fix: Show in a dedicated read-only dialog with monospace formatting and a Copy button. Can reuse the pattern from `TextCompareDialog` — a simple Window with a read-only TextBox and a Copy button. Or even simpler: a `ConfirmDialog`-style window with scrollable text.

---

### 17. Auto-expand results panel for DML queries

**File:** `QueryTabView.axaml.cs` → `OnResultsChanged()`

When running `UPDATE`/`INSERT`/`DELETE`, the results panel stays collapsed because there are no result sets. The "✓ 42 rows affected" flash lasts 3 seconds and fades. Easy to miss.

Fix: In `RunQueryAsync`, after execution, if `Results.Count == 0` but the query succeeded (no exception) and there are messages, auto-expand the results panel to show the Messages tab. The expansion logic already exists in `OnResultsChanged` for result sets — add a similar path for messages-only results.

---

### 18. Object Explorer filter: show "no matches" placeholder

**File:** `ObjectExplorerViewModel.cs` → `ApplyFilter()` and the OE AXAML

When the filter matches nothing, all nodes are hidden and the tree looks empty/broken.

Fix: Add an observable property `HasVisibleNodes` (or `ShowFilterEmptyState`). After `ApplyFilter()`, check if any root node is visible. In the AXAML, show a TextBlock "(No objects matching 'xyz')" when `HasVisibleNodes` is false and `FilterText` is non-empty.

---

### 19. Update download: add cancel option

**File:** `MainWindow.axaml.cs` → `OnUpdateNowClicked()`

Once "Update Now" is clicked, there's no way to cancel. If the download hangs, the user is stuck.

Fix: Add a CancellationTokenSource for the download. Change the "Later" button to function as "Cancel" during download (change its text to "Cancel"). On cancel, reset the "Update Now" button state.

---

### 20. Query history menu: better truncation

**File:** `MainWindow.axaml.cs` → `RebuildQueryHistoryMenu()`

Queries are truncated to 80 characters. For similar queries with the same prefix, all entries look identical.

Fix: Increase to 120 chars, and strip leading whitespace/blank lines/comment lines before truncating so the meaningful SQL shows first. Also deduplicate the display (if two entries would render identically after truncation, append a suffix like the database name or timestamp).

---

### 21. "Show only differences" checkbox: show progress immediately

**File:** `CompareViewModel.cs` → `OnShowOnlyDifferencesChanged()` / `ScanForDifferencesAsync()`

Toggling this checkbox on a large database starts a scan that could take 30+ seconds. The `IsScanning` flag is set inside `ScanForDifferencesAsync` but there's a gap between the checkbox toggle and the scan start where nothing visual happens.

Fix: Set `IsScanning = true` and `ScanProgressText = "Preparing scan..."` immediately in `OnShowOnlyDifferencesChanged` before calling `ScanForDifferencesAsync()`. Also show the total count: `ScanProgressText = $"Scanning 0/{total}..."`.

---

### 22. Intellisense cache: invalidate on DDL execution

**File:** `QueryEditorHost.axaml.cs` → `_intellisenseCache`

Once schema data is loaded for a connection+database pair, it's cached forever. New tables/columns created during the session won't appear in autocomplete.

Fix: After a successful query execution, check if the SQL contains DDL keywords (`CREATE`, `ALTER`, `DROP`). If so, remove the cache entry for that connection+database so the next tab switch or completion request reloads the schema.

```csharp
// In the QueryExecuted handler:
vm.QueryExecuted += (sql, db, rows) =>
{
    _sessionService?.AddQueryToHistory(sql, db, rows);

    // Invalidate intellisense cache if DDL was executed
    if (IsDdlStatement(sql) && vm.TabConnectionString != null && db != null)
    {
        var cacheKey = $"{vm.TabConnectionString}|{db}";
        _intellisenseCache.Remove(cacheKey);
    }
};

private static bool IsDdlStatement(string sql)
{
    var trimmed = sql.TrimStart();
    return trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
           trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase) ||
           trimmed.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase);
}
```

---

### 23. Views: add double-click action in Object Explorer

**File:** `QueryEditorHost.axaml.cs` → `OnTreeDoubleTapped()`

Tables → SELECT TOP 100, Procs → View Definition, but Views fall through to expand/collapse. Views are queryable and should behave like tables.

Fix: Add case:
```csharp
case ObjectExplorerNodeType.View:
    explorer.SelectTop100(node);
    e.Handled = true;
    break;
```

---

### 24. Functions: add double-click action in Object Explorer

**File:** `QueryEditorHost.axaml.cs` → `OnTreeDoubleTapped()`

Same gap as Views. Functions should open View Definition on double-click.

Fix: Add case:
```csharp
case ObjectExplorerNodeType.Function:
    _ = explorer.ViewDefinitionAsync(node);
    e.Handled = true;
    break;
```

---

### 25. ConnectOnStartup: implement or remove the checkbox

**File:** `ConnectionManagerDialog.axaml` (checkbox exists), `MainWindow.axaml.cs` → `OnOpened()`

The `ConnectOnStartup` property exists on `SavedConnection`, the UI checkbox exists in Connection Manager, but nothing in the startup flow checks it. Dead feature visible to users.

Fix: In `MainWindow.OnOpened`, before showing the Connection Dialog, check if any registry connections have `ConnectOnStartup == true`. If so, auto-connect them. If at least one succeeds, skip the Connection Dialog and go straight to the editor with that connection active.

```csharp
private async void OnOpened(object? sender, EventArgs e)
{
    Opened -= OnOpened;

    // Try auto-connecting saved connections
    var autoConnects = _registry.Connections
        .Where(c => c.Config.ConnectOnStartup)
        .ToList();

    foreach (var managed in autoConnects)
    {
        var (success, _) = await _registry.ConnectAsync(managed.Id);
        if (success)
        {
            // Use the first successful auto-connect as default
            var settings = new ConnectionSettings { ... };
            _viewModel.OnConnected(settings, managed.Config);
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.SetDefaultConnection(settings, managed.Config);
            // ... same post-connect setup as ShowConnectionDialogAsync ...
            return; // Skip showing the Connection Dialog
        }
    }

    // No auto-connect succeeded — show dialog as usual
    await ShowConnectionDialogAsync();
}
```

---

### 26. Keyboard shortcut for tab switching (Ctrl+Tab)

**File:** `MainWindow.axaml.cs` → `OnKeyDown()`

Standard in every tabbed application. Add:
- `Ctrl+Tab` → switch to next query tab
- `Ctrl+Shift+Tab` → switch to previous query tab

```csharp
if (ctrl && e.Key == Key.Tab && QueryEditorTab.IsChecked == true)
{
    var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
    if (host != null)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            host.SwitchToPreviousTab();
        else
            host.SwitchToNextTab();
    }
    e.Handled = true;
    return;
}
```

Add `SwitchToNextTab()` / `SwitchToPreviousTab()` methods in `QueryEditorHost`:
```csharp
public void SwitchToNextTab()
{
    if (_tabs.Count <= 1) return;
    var next = (_activeTabIndex + 1) % _tabs.Count;
    SwitchToTab(next);
}

public void SwitchToPreviousTab()
{
    if (_tabs.Count <= 1) return;
    var prev = (_activeTabIndex - 1 + _tabs.Count) % _tabs.Count;
    SwitchToTab(prev);
}
```

---

### 27. Confirm before deleting a connection in Connection Manager

**File:** `ConnectionManagerViewModel.cs` → `Delete()`

Currently removes the connection immediately with no confirmation. A production connection with a custom name and color is gone with one click.

Fix: Make `Delete` async, show a ConfirmDialog:
```csharp
[RelayCommand]
private async Task DeleteAsync()
{
    if (SelectedConnection == null) return;
    // Fire an event to request confirmation from the View
    // (same pattern as DeployRequested in CompareViewModel)
}
```

Or, since the Connection Manager dialog is already open, use the existing `ConfirmDialog`:
```csharp
public event Func<string, Task<bool>>? ConfirmRequested;

[RelayCommand]
private async Task DeleteAsync()
{
    if (SelectedConnection == null) return;
    if (ConfirmRequested != null)
    {
        var confirmed = await ConfirmRequested(
            $"Delete connection '{SelectedConnection.DisplayName}'?");
        if (!confirmed) return;
    }
    _registry.Remove(SelectedConnection.Id);
    SelectedConnection = Connections.FirstOrDefault();
}
```

Wire in `ConnectionManagerDialog.axaml.cs`:
```csharp
vm.ConfirmRequested += async message =>
{
    var dialog = new ConfirmDialog(message);
    await dialog.ShowDialog(this);
    return dialog.Confirmed;
};
```

---

### 28. Recent Files menu: show path context

**File:** `MainWindow.axaml.cs` → `RebuildRecentFilesMenu()`

Two files named "query.sql" in different folders look identical in the menu.

Fix: Show a subtle path suffix. Replace:
```csharp
var name = Path.GetFileNameWithoutExtension(path);
```
With:
```csharp
var name = Path.GetFileNameWithoutExtension(path);
var dir = Path.GetDirectoryName(path);
var shortDir = dir != null ? $"  ({Path.GetFileName(dir)})" : "";
var item = new MenuItem { Header = $"{name}{shortDir}", Tag = path };
```

This shows e.g., `query  (queries)` or `backup  (Desktop)`.

---

### 29. Export to Git: focused path prompt when unconfigured

**File:** `MainWindow.axaml.cs` → `ExportToGitAsync()`

Currently opens the full Settings dialog when no path is configured. The user has to find the Git Export field among all settings.

Fix: Show a native folder picker instead:
```csharp
if (string.IsNullOrEmpty(exportPath))
{
    var topLevel = TopLevel.GetTopLevel(this);
    var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(
        new FolderPickerOpenOptions { Title = "Select Git Export Folder" });
    if (folder.Count == 0) return;
    var selectedPath = folder[0].TryGetLocalPath();
    if (selectedPath == null) return;
    _settings.Settings.GitExportPath = selectedPath;
    _settings.Save();
    exportPath = selectedPath;
}
```

---

### 30. Duplicate Tab: fix database selection race (same as #12)

**File:** `QueryEditorHost.axaml.cs` → `DuplicateTab()`

Sets `newVm.SelectedDatabase = sourceVm.SelectedDatabase` before the new tab's database list is populated. Same race condition as session restore (#12).

Fix: Same approach — pass the desired database into `LoadDatabasesForTabAsync`:
```csharp
if (effectiveConn != null && effectiveConn != _primaryConnectionString)
{
    _ = LoadDatabasesForTabAsync(newVm, effectiveConn, sourceVm.SelectedDatabase);
}
else if (_cachedDatabases.Count > 0)
{
    newVm.SetDatabases(_cachedDatabases, sourceVm.SelectedDatabase);
}
```

---

## Deferred / Discussion

### 14. Autosave timer: only write when dirty

`QueryEditorHost.cs` — The 30-second autosave timer writes session.json on every tick, even if nothing changed. On the UI thread via `Dispatcher.UIThread.Post`, this could cause micro-hitches during JSON serialization.

**Idea:** Add a `_sessionDirty` flag, flip it on tab create/close/switch/text-change, only write+reset if dirty. Alternatively, move serialization off the UI thread.
