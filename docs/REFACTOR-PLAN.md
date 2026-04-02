# Lookout Refactoring Plan — Smooth-icisize Edition

**Goal**: Reduce file sizes, improve navigability, and make the codebase easier to work on — without changing any public APIs or breaking behavior.

**Principle**: Every step must be independently testable. Build, run, verify after each step. No behavior changes.

---

## Progress Summary (April 2026)

Phases completed so far — all built, launched, and manually verified:

| Step | What | Result |
|------|------|--------|
| Warm-up | Extracted `OccurrenceHighlighter`, `ExecutionFlashHighlighter`, `BracketHighlighter` from QueryTabView → `Rendering/` folder | 3 standalone classes in own files |
| **Phase 1A** | Split `QueryTabView.axaml.cs` | 2,913 → 312 lines (8 files: `.axaml.cs`, `.Editor.cs`, `.Intellisense.cs`, `.EditMode.cs`, `.Export.cs`, `.DragDrop.cs`, `.Peek.cs`, `.Results.cs`) |
| **Phase 1B** | Split `QueryEditorHost.axaml.cs` | 2,260 → 214 lines (6 files: `.axaml.cs`, `.Tabs.cs`, `.Session.cs`, `.OeRouting.cs`, `.Database.cs`, `.FileOps.cs`) + `HistoryDisplayItem` → `Models/` |
| **Phase 1C** | Split `DatabaseService.cs` | 2,698 → 559 lines (8 files: `.cs`, `.Schema.cs`, `.VersionHistory.cs`, `.Compare.cs`, `.Jobs.cs`, `.Activity.cs`, `.Index.cs`, `.TableOps.cs`) |
| **Phase 1D** | Split `MainWindow.axaml.cs` | 1,757 → 310 lines (5 files: `.axaml.cs`, `.Menus.cs`, `.Connection.cs`, `.KeyBindings.cs`, `.StatusBar.cs`) |
| **Phase 1E** | Split `CompareViewModel.cs` | 1,907 → 570 lines (4 files: `.cs`, `.Scan.cs`, `.Deploy.cs`, `.DataCompare.cs`) |

**Phase 1 complete. Resume from: Phase 2 (Small Extractions)**

---

## The Problem (by the numbers)

| File | Original Size | Current Size | Status |
|------|---------------|--------------|--------|
| `QueryTabView.axaml.cs` | 108 KB / 2,913 lines | 312 lines | DONE — split into 8 files |
| `QueryEditorHost.axaml.cs` | 88 KB / 2,260 lines | 214 lines | DONE — split into 6 files |
| `DatabaseService.cs` | 107 KB / 2,698 lines | 559 lines | DONE — split into 8 files |
| `MainWindow.axaml.cs` | 69 KB / 1,757 lines | 310 lines | DONE — split into 5 files |
| `CompareViewModel.cs` | 61 KB / 1,907 lines | 570 lines | DONE — split into 4 files |
| `ObjectExplorerViewModel.cs` | 49 KB / 1,322 lines | 1,322 lines | Deferred — discuss after others |

---

## Phase 1: Partial Classes (Zero Risk) — ALL DONE

C# `partial class` lets us split a class across files. Same class, same namespace, same behavior — just organized into files by responsibility. The compiler merges them. **Nothing changes at runtime.**

### 1A. Split `QueryTabView.axaml.cs` (108 KB → 8 files) — DONE

The file has clear section comments already. Split along those boundaries:

| New File | Contents | Approx Size |
|----------|----------|-------------|
| `QueryTabView.axaml.cs` | Fields, constructor, Initialize(), InsertText(), HandleKeyDown(), RefreshTheme(), property accessors | ~15 KB |
| `QueryTabView.Editor.cs` | ConfigureEditor(), syntax highlighting, occurrence highlighting, bracket matching, code folding, selection flash, BEGIN/END matching, move lines, go-to-line, comment/uncomment, word wrap, text transform, zoom | ~25 KB |
| `QueryTabView.Intellisense.cs` | OnTextEntering(), OnTextEntered(), ShowCompletionWindow(), completion lifecycle | ~5 KB |
| `QueryTabView.Results.cs` | Result tabs (RebuildResultTabs, SelectResultTab, SelectMessagesTab, pinning, context menus), results panel collapse/expand/maximize, BuildColumns(), column freeze | ~25 KB |
| `QueryTabView.EditMode.cs` | OnEditModeChanged(), row state colors, LoadingRow, RowEditEnded, edit context menu, Show SQL preview, add/delete row, paste, undo, cell detail panel, null styling | ~20 KB |
| `QueryTabView.Export.cs` | Export menu, ExportResultsAsync(), CSV/JSON/TSV formatters, Copy as INSERT, Copy with Headers, Copy Cell, Filter by Value | ~15 KB |
| `QueryTabView.DragDrop.cs` | Editor drag-over/drop, proc drop, file drop, insert at drop position | ~3 KB |
| `QueryTabView.Peek.cs` | Peek definition, editor context menu, Cmd+Click/Shift+Click handlers | ~5 KB |

Also move these to their own files (they're separate classes, not partial):
- `OccurrenceHighlighter` → `Rendering/OccurrenceHighlighter.cs`
- `ExecutionFlashHighlighter` → `Rendering/ExecutionFlashHighlighter.cs`
- `BracketHighlighter` → `Rendering/BracketHighlighter.cs`

### 1B. Split `QueryEditorHost.axaml.cs` (88 KB → 6 files) — DONE

| New File | Contents | Approx Size |
|----------|----------|-------------|
| `QueryEditorHost.axaml.cs` | Fields, constructor, Initialize(), RefreshTheme(), SetDefaultConnection(), event declarations | ~12 KB |
| `QueryEditorHost.Tabs.cs` | AddNewTab(), CloseTabAsync(), SwitchToTab(), SwitchToNext/Previous, DuplicateTab(), RebuildTabStrip(), BuildTabContextMenu(), tab drag-to-reorder, SyncToolbarWithActiveTab() | ~25 KB |
| `QueryEditorHost.Session.cs` | SaveSession(), RestoreSession(), autosave timer, query history panel, ToggleHistoryPanel(), RefreshHistoryGrid() | ~10 KB |
| `QueryEditorHost.OeRouting.cs` | All OE event handlers: OnInsertText, OnInsertAtCursor, OnEditDataRequested, OnPeekDefinition, OnQuickExecute, OnShowDependencies, OnAlterSequence, OnResetSequence, OnStartJob, ShowTableProperties, ShowContextMenu(), OnTreeDoubleTapped(), OnTreePointerReleased(), drag-from-OE | ~25 KB |
| `QueryEditorHost.Database.cs` | ReloadDatabasesAsync(), LoadDatabasesForTabAsync(), server cache, intellisense cache, OnTabDatabaseChanged(), ToggleObjectExplorer(), RestoreObjectExplorerState() | ~10 KB |
| `QueryEditorHost.FileOps.cs` | SaveActiveQueryAsync(), SaveAsActiveQueryAsync(), OpenQueryAsync(), OpenQueryFromPath(), OpenDroppedFile(), QuickQuoteSelection(), FormatSqlInEditor() | ~8 KB |

Also move:
- `HistoryDisplayItem` → `Models/HistoryDisplayItem.cs`

### 1C. Split `DatabaseService.cs` (107 KB → 8 files) — DONE

| New File | Contents | Approx Size |
|----------|----------|-------------|
| `DatabaseService.cs` | Connection management, BuildConnectionString(), TestConnection(), GetDatabases(), ExecuteQuery (core query runner) | ~15 KB |
| `DatabaseService.Schema.cs` | GetTables/Views/Procs/Functions/Triggers/Sequences, GetColumns, GetAllColumns, GetObjectDefinition, GetProcParameters — everything the Object Explorer needs | ~25 KB |
| `DatabaseService.VersionHistory.cs` | GetRecentChanges, GetObjectVersions, SyncFromAuditLog, RollbackToVersion, code search, dependencies | ~20 KB |
| `DatabaseService.Compare.cs` | Compare-related queries (GetObjectsForCompare, deploy, ConvertToCreateOrAlter) | ~15 KB |
| `DatabaseService.Jobs.cs` | Job queries: GetJobs, GetJobSteps, GetJobHistory, StartJob, GetJobSchedules | ~10 KB |
| `DatabaseService.Index.cs` | Index analysis: unused indexes, missing indexes, duplicate/overlapping indexes | ~15 KB |
| `DatabaseService.TableOps.cs` | GetTableProperties, GenerateCreateTableScript, AlterSequenceRestart, ToggleTrigger | ~7 KB |

### 1D. Split `MainWindow.axaml.cs` (69 KB → 5 files) — DONE

| New File | Contents |
|----------|----------|
| `MainWindow.axaml.cs` | Fields, constructor, lifecycle (OnOpened, OnClosing), RestoreWindowPosition, helpers (GetActiveEditor, OpenSearchPanel, etc.) |
| `MainWindow.Menus.cs` | WireMenuItems(), all File/Edit/View/Tools/Help menu handlers, RebuildRecentFilesMenu, RebuildQueryHistoryMenu, ExportToGit, dialogs |
| `MainWindow.Connection.cs` | ShowConnectionDialogAsync, ChangeConnectionAsync, reconnect flow, quick-switch buttons, auto-update, crash banner handlers |
| `MainWindow.KeyBindings.cs` | OnKeyDown (all keyboard shortcut routing), Command Palette (BuildCommandRegistry, Show/Hide/Filter/Execute) |
| `MainWindow.StatusBar.cs` | UpdateStatusBar, BindActiveQueryTab, OnQueryFlash, GetBrush |

### 1E. Split `CompareViewModel.cs` (61 KB → 4 files) — DONE

| New File | Contents |
|----------|----------|
| `CompareViewModel.cs` | Fields, properties, constructor, connection management (Connect/Password/BuildConnectionString), toggle commands, Refresh, Copy, AddConnection, CompareObject class |
| `CompareViewModel.Scan.cs` | LoadObjectsAsync, LoadCodeObjectsAsync, LoadTableObjectsAsync, ScanForDifferencesAsync, FilterObjects, LoadDefinitionsAsync, GetDefinitionAsync, UpdateDiff |
| `CompareViewModel.Deploy.cs` | DeployAsync, Deploy2Async, DeployTableAsync, DeployColumnAsync, DeploySelectedAsync (batch), SelectAll/DeselectAll |
| `CompareViewModel.DataCompare.cs` | CompareDataAsync, ApplyDataFilter, data row selection/fields, DeployDataRowsAsync, DeployFieldAsync, ShowDataDeploySql |

---

## Phase 2: Small Extractions (Low Risk)

After partial class splits are done and verified:

### 2A. Move misplaced classes to proper homes

- `HistoryDisplayItem` (bottom of QueryEditorHost.axaml.cs) → `Models/HistoryDisplayItem.cs`
- `OccurrenceHighlighter`, `ExecutionFlashHighlighter`, `BracketHighlighter` (bottom of QueryTabView.axaml.cs) → `Rendering/` folder
- `CachedServerData` (nested in QueryEditorHost) → can stay nested, or promote to Models if used elsewhere

### 2B. Extract `FormatTimeAgo` to a shared helper

Currently duplicated between `QueryEditorHost.FormatTimeAgo()` (static method) and `HistoryDisplayItem.TimeAgo` (property with identical logic).

Create `Helpers/TimeFormatter.cs`:
```csharp
public static class TimeFormatter
{
    public static string FormatTimeAgo(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return when.ToString("MMM d");
    }
}
```

### 2C. Extract `FindBrush` helper

Both `QueryTabView` and `QueryEditorHost` have identical brush-lookup helpers:
```csharp
private IBrush FindBrush(string key) =>
    Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
        ? b : Brushes.Transparent;
```

Add to `ThemeManager` as a static helper: `ThemeManager.GetBrush("key")`.

### 2D. Consolidate schema.name parsing

The pattern `string schema = "dbo", name = objectName; if (objectName.Contains('.')) { ... }` appears at least 5 times in QueryEditorHost alone. Extract to a helper:
```csharp
// In Helpers/SqlNameParser.cs or as a static method on DatabaseService
public static (string Schema, string Name) ParseSchemaQualifiedName(string objectName)
{
    if (objectName.Contains('.'))
    {
        var parts = objectName.Split('.', 2);
        return (parts[0].Trim('[', ']'), parts[1].Trim('[', ']'));
    }
    return ("dbo", objectName.Trim('[', ']'));
}
```

---

## Phase 3: Future Consideration (Not Now)

These are things noticed during the audit but would be higher risk or lower priority:

- **QueryEditorHostViewModel is 492 bytes** while its code-behind is 88KB. Eventually some of the tab management and OE routing logic could migrate to the ViewModel for better testability. But this is a major refactor — not for today.
- **Empty catch blocks** — there are several `catch { }` that silently swallow errors. These should at minimum get `AppLogger.Log()` calls, but that's a separate sweep.
- **Event subscription cleanup** — many `.Click +=` and `.PropertyChanged +=` lambda subscriptions that never get unsubscribed. Not causing visible bugs, but technically a memory concern for long sessions.

---

## Execution Order

1. ~~**Phase 1A** — Split QueryTabView.axaml.cs → build & test~~ DONE
2. ~~**Phase 1B** — Split QueryEditorHost.axaml.cs → build & test~~ DONE
3. ~~**Phase 1C** — Split DatabaseService.cs → build & test~~ DONE
4. ~~**Phase 1D** — Split MainWindow.axaml.cs → build & test~~ DONE
5. ~~**Phase 1E** — Split CompareViewModel.cs → build & test~~ DONE
6. **Phase 2** — Small extractions → build & test ← RESUME HERE

---

## How to Do a Partial Class Split

For CC's reference — the mechanical process:

1. Open the source file (e.g. `QueryTabView.axaml.cs`)
2. Create the new file (e.g. `QueryTabView.Editor.cs`)
3. Add the same namespace, usings, and class declaration with `partial`:
```csharp
// In QueryTabView.Editor.cs
using System.Collections.Specialized;
// ... (copy relevant usings from main file)

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    // Methods cut from the main file go here
}
```
4. Cut the relevant methods from the main file, paste into the new file
5. Add any `using` statements the moved methods need
6. Build. Fix any missing references. Test.

**Important**: The `partial` keyword must be on the class declaration in ALL files. The main `.axaml.cs` file is already `partial` (Avalonia generates the other half from XAML). Just make sure the new files also say `partial`.

**Important**: Only the main `.axaml.cs` file should have `InitializeComponent()` and the constructor. The partial files just have methods.

**Important**: Private fields that are used across multiple partial files stay in the main file (or whichever file makes most sense). The compiler sees them all as one class, so it doesn't matter which file they're declared in — but keeping fields in the main file is conventional.

**Important**: Do NOT change the base class on partial files. Only the main `.axaml.cs` specifies `: UserControl` or `: Window`. The partials just say `public partial class QueryTabView` with no base.

**Important for DatabaseService**: Since it's a regular class (not a View), all partial files are equal — there's no "main" file. Just make sure exactly one file has the constructor and the shared `_connectionString` field.
