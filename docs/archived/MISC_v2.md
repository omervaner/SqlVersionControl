# Lookout — QoL Features Batch (MISC_v2)

Date: March 30, 2026  
Priority: Ship-readiness features — daily workflow improvements inspired by SSMS/ADS comparison.

**READ FIRST:** `CLAUDE.md` and `docs/IMPROVEMENTS.md` — understand the project structure and recent changes before starting.

**RULE:** Do NOT batch more than 2–3 items per commit. Test each feature compiles before moving to the next. Follow all existing patterns from CLAUDE.md (single source of truth, dynamic resources, etc.).

---

## 1. Table Row Counts in Object Explorer

**Priority:** HIGH — instant metadata, zero server cost.  
**Files:** `Services/DatabaseService.cs`, `ViewModels/ObjectExplorerViewModel.cs`, `Models/ObjectExplorerNode.cs`

### What
When the "Tables" folder expands in Object Explorer, fetch approximate row counts from `sys.dm_db_partition_stats` (metadata-only, no table scans, returns in <50ms even on databases with thousands of tables) and display them inline: `Orders (1.2M)` or `t_inventory (847K)`.

### Implementation

**Step 1: Add method to `DatabaseService.cs`:**

```csharp
public async Task<Dictionary<string, long>> GetTableRowCountsAsync(string connectionString, string database)
{
    var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var connStr = BuildConnectionString(connectionString, database);
    using var conn = new SqlConnection(connStr);
    await conn.OpenAsync();

    var sql = @"
        SELECT s.name + '.' + t.name, SUM(p.rows)
        FROM sys.tables t
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        JOIN sys.dm_db_partition_stats p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
        GROUP BY s.name, t.name";

    using var cmd = new SqlCommand(sql, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result[reader.GetString(0)] = reader.GetInt64(1);

    return result;
}
```

Also add an overload without explicit connection string that uses `_connectionString`.

**Step 2: Add `RowCount` property to `ObjectExplorerNode.cs`:**

```csharp
[ObservableProperty] private long _rowCount = -1; // -1 = not loaded

partial void OnRowCountChanged(long value) => OnPropertyChanged(nameof(DisplayName));
```

**Step 3: Update `DisplayName` for Tables in `ObjectExplorerNode.cs`:**

In the `DisplayName` switch, for `ObjectExplorerNodeType.Table`:

```csharp
ObjectExplorerNodeType.Table =>
    FormatTableDisplayName(),
```

Add helper:
```csharp
private string FormatTableDisplayName()
{
    var baseName = string.IsNullOrEmpty(Schema) || Schema == "dbo" ? Name : $"{Schema}.{Name}";
    if (RowCount < 0) return baseName; // not loaded yet
    return $"{baseName} ({FormatRowCount(RowCount)})";
}

private static string FormatRowCount(long count) => count switch
{
    < 1_000 => count.ToString(),
    < 1_000_000 => $"{count / 1_000.0:F1}K",
    < 1_000_000_000 => $"{count / 1_000_000.0:F1}M",
    _ => $"{count / 1_000_000_000.0:F1}B"
};
```

**Step 4: Fetch and apply in `ObjectExplorerViewModel.cs` → `LoadFolderChildrenAsync()`:**

After loading tables in the `"Tables"` case, fire-and-forget a row count fetch:

```csharp
case "Tables":
    var tables = connStr != null
        ? await _db.GetTablesAsync(connStr, db)
        : await _db.GetTablesAsync(db);
    var tableNodes = tables.Select(t => WireChild(new ObjectExplorerNode
    {
        Name = t.Name, Schema = t.Schema, DatabaseName = db,
        NodeType = ObjectExplorerNodeType.Table,
        Children = [ObjectExplorerNode.CreateDummy()]
    }, folderNode)).ToList();
    await AddChildrenInBatchesAsync(folderNode, tableNodes);

    // Fire-and-forget: fetch row counts from metadata (instant, no table scans)
    _ = Task.Run(async () =>
    {
        try
        {
            var effectiveConn = connStr ?? _activeConnectionString;
            if (effectiveConn == null) return;
            var counts = await _db.GetTableRowCountsAsync(effectiveConn, db);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var node in tableNodes)
                {
                    var key = $"{(string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema)}.{node.Name}";
                    if (counts.TryGetValue(key, out var count))
                        node.RowCount = count;
                }
            });
        }
        catch { /* best effort */ }
    });
    break;
```

### Result
Tables folder shows: `Orders (1.2M)`, `t_grt_inventory (3.4M)`, `Customers (45.7K)`, `Config (12)`.

---

## 2. Cell Detail Viewer on Results Grid

**Priority:** HIGH — biggest daily friction point for anyone working with long text, JSON, or XML columns.  
**Files:** `Views/QueryTabView.axaml`, `Views/QueryTabView.axaml.cs`

### What
When the user clicks a cell in the results grid, show the full cell value in a read-only panel below the grid (or as a resizable bottom strip). For long text, JSON, and XML values that are truncated in the grid cell, this is the only way to read them without copy-pasting.

### Implementation

**Step 1: Add a detail strip below the results grid in `QueryTabView.axaml`:**

Inside the `<Panel Grid.Row="1">` that contains `ResultsGrid`, `MessagesPanel`, etc., add a new panel that sits at the bottom:

```xml
<!-- Cell Detail Strip (visible when a cell is selected) -->
<Border x:Name="CellDetailPanel" IsVisible="False"
        VerticalAlignment="Bottom" Height="100"
        Background="{DynamicResource PanelHeaderBackground}"
        BorderBrush="{DynamicResource BorderDefault}" BorderThickness="0,1,0,0">
    <Grid RowDefinitions="Auto,*">
        <Border Grid.Row="0" Padding="8,2" Height="22">
            <Grid ColumnDefinitions="*,Auto,Auto">
                <TextBlock x:Name="CellDetailHeader" FontSize="11"
                           Foreground="{DynamicResource TextSecondary}"
                           VerticalAlignment="Center"/>
                <Button Grid.Column="1" x:Name="CellDetailCopyButton"
                        Content="Copy" Classes="btn-secondary"
                        Padding="8,1" FontSize="10" Height="18" MinHeight="0"
                        Cursor="Hand" Margin="4,0"/>
                <Button Grid.Column="2" x:Name="CellDetailCloseButton"
                        Content="×" Background="Transparent"
                        Foreground="{DynamicResource TextSecondary}"
                        Padding="4,0" FontSize="14" MinWidth="0" MinHeight="0"
                        BorderThickness="0" Cursor="Hand"/>
            </Grid>
        </Border>
        <ScrollViewer Grid.Row="1" Padding="8,4"
                      HorizontalScrollBarVisibility="Auto"
                      VerticalScrollBarVisibility="Auto">
            <TextBlock x:Name="CellDetailText"
                       FontFamily="Consolas, Menlo, Monaco, monospace"
                       FontSize="12" TextWrapping="Wrap"
                       Foreground="{DynamicResource TextPrimary}"
                       IsTextSelectionEnabled="True"/>
        </ScrollViewer>
    </Grid>
</Border>
```

**Important:** This panel should overlay the bottom of the results grid area (VerticalAlignment="Bottom"), not take space from the layout. Alternatively, put it in the same Grid row as ResultsGrid with a Z-index or use a panel that overlays.

**Step 2: Wire cell selection in `QueryTabView.axaml.cs`:**

In `Initialize()`:
```csharp
ResultsGrid.SelectionChanged += OnResultsGridSelectionChanged;
CellDetailCopyButton.Click += async (_, _) =>
{
    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        await clipboard.SetTextAsync(CellDetailText.Text ?? "");
};
CellDetailCloseButton.Click += (_, _) => CellDetailPanel.IsVisible = false;
```

Add the handler:
```csharp
private void OnResultsGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
{
    if (ResultsGrid.SelectedItem == null || ResultsGrid.CurrentColumn == null)
    {
        CellDetailPanel.IsVisible = false;
        return;
    }

    var colIndex = ResultsGrid.Columns.IndexOf(ResultsGrid.CurrentColumn);
    if (colIndex < 0) { CellDetailPanel.IsVisible = false; return; }

    var colName = ResultsGrid.CurrentColumn.Header?.ToString() ?? "";
    object? cellValue = null;

    if (ResultsGrid.SelectedItem is object?[] row && colIndex < row.Length)
        cellValue = row[colIndex];
    else if (ResultsGrid.SelectedItem is EditableRow editRow)
        cellValue = editRow[colIndex];

    if (cellValue == null || cellValue == DBNull.Value)
    {
        CellDetailHeader.Text = $"{colName}: NULL";
        CellDetailText.Text = "NULL";
    }
    else
    {
        var text = cellValue.ToString() ?? "";
        var length = text.Length;
        CellDetailHeader.Text = $"{colName} ({length:N0} chars)";
        CellDetailText.Text = text;
    }

    CellDetailPanel.IsVisible = true;
}
```

**Step 3:** Hide `CellDetailPanel` when switching result tabs, entering edit mode, or when results are cleared. Add `CellDetailPanel.IsVisible = false;` in `SelectResultTab()`, `SelectMessagesTab()`, `SelectTraceTab()`, and `OnEditModeChanged()`.

### Notes
- Consider detecting JSON/XML and applying basic formatting (indent) in the detail view. Not required for v1 but a nice touch later.
- The detail panel height could be user-resizable via a GridSplitter. Start with fixed 100px for simplicity.

---

## 3. Results Grid Right-Click Context Menu

**Priority:** HIGH — every SQL IDE has this, users will notice its absence.  
**Files:** `Views/QueryTabView.axaml.cs`

### What
Right-click on a cell in the results grid shows a context menu with: Copy Cell Value, Copy Row (tab-delimited), Copy Row as INSERT, Copy All as INSERT, Filter by This Value (opens new tab with `WHERE col = <value>`).

### Implementation

**Step 1: Add context menu builder in `QueryTabView.axaml.cs`:**

In `Initialize()`, add:
```csharp
ResultsGrid.AddHandler(PointerReleasedEvent, OnResultsGridPointerReleased,
    Avalonia.Interactivity.RoutingStrategies.Tunnel);
```

Add the handler:
```csharp
private void OnResultsGridPointerReleased(object? sender, PointerReleasedEventArgs e)
{
    if (e.InitialPressMouseButton != MouseButton.Right) return;
    if (_viewModel?.IsEditMode == true) return; // Edit mode has its own context menu

    // Find which cell was clicked
    var source = e.Source as Avalonia.Visual;
    var cell = source?.FindAncestorOfType<DataGridCell>();
    if (cell == null) return;

    var row = cell.FindAncestorOfType<DataGridRow>();
    if (row == null) return;

    var colIndex = ResultsGrid.Columns.IndexOf(cell.Column);
    var rowIndex = row.GetIndex();
    if (colIndex < 0 || rowIndex < 0) return;

    var result = GetCurrentResult();
    if (result == null) return;
    if (rowIndex >= result.Rows.Count) return;

    var cellValue = result.Rows[rowIndex][colIndex];
    var colName = result.ColumnNames[colIndex];

    var menu = new ContextMenu();

    // Copy Cell Value
    var copyCell = new MenuItem { Header = "Copy Cell Value" };
    var cellText = cellValue == null || cellValue == DBNull.Value ? "NULL" : cellValue.ToString() ?? "";
    copyCell.Click += async (_, _) =>
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(cellText);
    };
    menu.Items.Add(copyCell);

    // Copy Row (tab-delimited)
    var copyRow = new MenuItem { Header = "Copy Row" };
    copyRow.Click += async (_, _) =>
    {
        var rowData = result.Rows[rowIndex];
        var values = rowData.Select(v => v == null || v == DBNull.Value ? "NULL" : v.ToString() ?? "");
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.Join("\t", values));
    };
    menu.Items.Add(copyRow);

    menu.Items.Add(new Separator());

    // Copy Row as INSERT
    var copyInsert = new MenuItem { Header = "Copy as INSERT" };
    copyInsert.Click += async (_, _) =>
    {
        var sql = GenerateInsertFromRow(result, rowIndex);
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(sql);
    };
    menu.Items.Add(copyInsert);

    // Copy All as INSERT
    if (result.Rows.Count <= 1000) // safety limit
    {
        var copyAllInsert = new MenuItem { Header = $"Copy All as INSERT ({result.Rows.Count} rows)" };
        copyAllInsert.Click += async (_, _) =>
        {
            var sb = new StringBuilder();
            for (int i = 0; i < result.Rows.Count; i++)
            {
                sb.AppendLine(GenerateInsertFromRow(result, i));
            }
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(sb.ToString());
        };
        menu.Items.Add(copyAllInsert);
    }

    menu.Items.Add(new Separator());

    // Filter by This Value (open new tab with WHERE clause)
    if (cellValue != null && cellValue != DBNull.Value)
    {
        var filterItem = new MenuItem { Header = $"Filter by '{Truncate(cellText, 30)}'" };
        filterItem.Click += (_, _) =>
        {
            // Determine the source table from the original query if possible
            var whereClause = IsNumericType(result.ColumnTypes[colIndex])
                ? $"WHERE [{colName}] = {cellText}"
                : $"WHERE [{colName}] = '{cellText.Replace("'", "''")}'";
            var sql = result.SourceSql != null
                ? $"-- Filter from results\nSELECT * FROM (\n{result.SourceSql}\n) sub\n{whereClause}"
                : $"-- TODO: add table name\nSELECT * FROM [???]\n{whereClause}";
            FilterByValueRequested?.Invoke(sql);
        };
        menu.Items.Add(filterItem);
    }

    menu.Open(cell);
    e.Handled = true;
}

/// <summary>Fired when "Filter by Value" is clicked — host should open a new tab.</summary>
public event Action<string>? FilterByValueRequested;

private static string GenerateInsertFromRow(QueryResult result, int rowIndex)
{
    var cols = string.Join(", ", result.ColumnNames.Select(c => $"[{c}]"));
    var vals = string.Join(", ", result.Rows[rowIndex].Select((v, i) =>
        FormatValueForInsert(v, result.ColumnTypes[i])));
    return $"INSERT INTO [???] ({cols}) VALUES ({vals})";
}

private static string FormatValueForInsert(object? value, Type colType)
{
    if (value == null || value == DBNull.Value) return "NULL";
    if (colType == typeof(bool)) return (bool)value ? "1" : "0";
    if (colType == typeof(DateTime) || colType == typeof(DateTimeOffset))
        return $"'{value}'";
    if (IsNumericType(colType)) return value.ToString() ?? "NULL";
    return $"'{value.ToString()?.Replace("'", "''") ?? ""}'";
}

private static bool IsNumericType(Type t) =>
    t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
    t == typeof(decimal) || t == typeof(double) || t == typeof(float);

private static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 3)] + "...";

private QueryResult? GetCurrentResult()
{
    if (_viewModel == null) return null;
    var idx = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
        ? _selectedTabIndex : 0;
    return idx < _viewModel.Results.Count ? _viewModel.Results[idx] : null;
}
```

**Step 2: Wire `FilterByValueRequested` in `QueryEditorHost.axaml.cs`:**

In `AddNewTab()` where other tab events are wired:
```csharp
tabView.FilterByValueRequested += sql => OpenScriptInNewTab(sql);
```

### Notes
- The `[???]` placeholder in generated INSERT is intentional — we don't always know the target table from the result set. If `SourceSql` contains a simple `FROM [schema].[table]`, parse it and fill in the table name.
- `Copy All as INSERT` is capped at 1000 rows to prevent clipboard bombs.

---

## 4. Export as JSON from Results Grid

**Priority:** MEDIUM — professional touch, paid product expectation.  
**Files:** `Views/QueryTabView.axaml.cs`

### What
Extend the existing Export button to offer CSV (existing) and JSON options.

### Implementation

Change the Export button click handler to show a flyout or context menu with format options instead of going straight to CSV:

```csharp
private void OnExportClicked(object? sender, RoutedEventArgs e)
{
    var menu = new MenuFlyout();

    var csvItem = new MenuItem { Header = "Export as CSV" };
    csvItem.Click += async (_, _) => await ExportResultsAsync("csv");
    menu.Items.Add(csvItem);

    var jsonItem = new MenuItem { Header = "Export as JSON" };
    jsonItem.Click += async (_, _) => await ExportResultsAsync("json");
    menu.Items.Add(jsonItem);

    var tsvItem = new MenuItem { Header = "Export as Tab-Delimited" };
    tsvItem.Click += async (_, _) => await ExportResultsAsync("tsv");
    menu.Items.Add(tsvItem);

    menu.ShowAt(ExportButton, true);
}
```

For JSON export, generate an array of objects:
```csharp
private static string ResultToJson(QueryResult result)
{
    var sb = new StringBuilder();
    sb.AppendLine("[");
    for (int r = 0; r < result.Rows.Count; r++)
    {
        sb.Append("  {");
        for (int c = 0; c < result.ColumnNames.Length; c++)
        {
            if (c > 0) sb.Append(", ");
            var val = result.Rows[r][c];
            sb.Append($"\"{result.ColumnNames[c]}\": ");
            if (val == null || val == DBNull.Value)
                sb.Append("null");
            else if (val is bool b)
                sb.Append(b ? "true" : "false");
            else if (IsNumericType(val.GetType()))
                sb.Append(val);
            else
                sb.Append($"\"{val.ToString()?.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        }
        sb.Append(r < result.Rows.Count - 1 ? "}," : "}");
        sb.AppendLine();
    }
    sb.AppendLine("]");
    return sb.ToString();
}
```

Use the native file picker with appropriate extension filter for each format.

---

## 5. Bracket/Parenthesis Matching in Editor

**Priority:** MEDIUM — expected in any code editor, essential for nested subqueries.  
**Files:** `Views/QueryTabView.axaml.cs`

### What
When the cursor is on `(`, `)`, `BEGIN`, or `END`, highlight the matching counterpart. AvaloniaEdit has built-in `BracketHighlightRenderer` support.

### Implementation

**Step 1: Add bracket highlighting in `ConfigureEditor()`:**

```csharp
// Bracket matching
var bracketRenderer = new AvaloniaEdit.Rendering.BracketHighlightRenderer(SqlEditor.TextArea.TextView);
SqlEditor.TextArea.Caret.PositionChanged += (_, _) =>
{
    var offset = SqlEditor.CaretOffset;
    var doc = SqlEditor.Document;
    if (offset <= 0 || offset > doc.TextLength) return;

    // Check character before and at caret
    var result = FindMatchingBracket(doc.Text, offset);
    if (result != null)
        bracketRenderer.SetHighlight(new AvaloniaEdit.Rendering.BracketSearchResult(
            result.Value.openOffset, 1, result.Value.closeOffset, 1));
    else
        bracketRenderer.SetHighlight(null);
};
```

**Step 2: Add bracket matching logic:**

```csharp
private static (int openOffset, int closeOffset)? FindMatchingBracket(string text, int offset)
{
    // Check character at offset-1 (cursor is AFTER the bracket)
    if (offset > 0)
    {
        var ch = text[offset - 1];
        if (ch == '(') return FindClosing(text, offset - 1, '(', ')');
        if (ch == ')') return FindOpening(text, offset - 1, '(', ')');
    }
    // Check character at offset (cursor is BEFORE the bracket)
    if (offset < text.Length)
    {
        var ch = text[offset];
        if (ch == '(') return FindClosing(text, offset, '(', ')');
        if (ch == ')') return FindOpening(text, offset, '(', ')');
    }
    return null;
}

private static (int openOffset, int closeOffset)? FindClosing(string text, int openPos, char open, char close)
{
    int depth = 1;
    for (int i = openPos + 1; i < text.Length && depth > 0; i++)
    {
        if (text[i] == open) depth++;
        else if (text[i] == close) { depth--; if (depth == 0) return (openPos, i); }
    }
    return null;
}

private static (int openOffset, int closeOffset)? FindOpening(string text, int closePos, char open, char close)
{
    int depth = 1;
    for (int i = closePos - 1; i >= 0 && depth > 0; i--)
    {
        if (text[i] == close) depth++;
        else if (text[i] == open) { depth--; if (depth == 0) return (i, closePos); }
    }
    return null;
}
```

**Note:** `BracketHighlightRenderer` is part of AvaloniaEdit. Check the exact API — it may use `SetHighlight(BracketSearchResult?)` or a similar method. If the built-in renderer isn't available, create a custom `DocumentColorizingTransformer` (same pattern as `OccurrenceHighlighter`) that highlights the matching bracket pair with a subtle background color from theme resources.

### Theme Support
Add bracket highlight colors to both `AppTheme.axaml` and `AppThemeLight.axaml`:
```xml
<SolidColorBrush x:Key="BracketMatchBackground" Color="#40808080"/>
```

---

## 6. Code Folding in Editor

**Priority:** MEDIUM — premium differentiator vs SSMS, essential for long stored procedures.  
**Files:** `Views/QueryTabView.axaml.cs`, new file `Services/SqlFoldingStrategy.cs`

### What
Enable code folding in the SQL editor for BEGIN/END blocks, multi-line comments, and CREATE...AS bodies. AvaloniaEdit supports `FoldingManager` natively.

### Implementation

**Step 1: Create `Services/SqlFoldingStrategy.cs`:**

```csharp
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace SqlVersionControl.Services;

public class SqlFoldingStrategy
{
    public IEnumerable<NewFolding> CreateNewFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var text = document.Text;

        // Fold BEGIN...END blocks
        FoldBeginEnd(text, foldings);

        // Fold multi-line comments /* ... */
        FoldBlockComments(text, foldings);

        // Fold CREATE...AS to END (entire procedure/function body)
        FoldCreateBlocks(text, foldings);

        // Sort by start offset (required by FoldingManager)
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    private static void FoldBeginEnd(string text, List<NewFolding> foldings)
    {
        // Simple stack-based BEGIN/END matching
        // Skip BEGIN/END inside strings and comments for accuracy
        var stack = new Stack<int>();
        var i = 0;
        while (i < text.Length)
        {
            // Skip strings
            if (text[i] == '\'') { i++; while (i < text.Length && text[i] != '\'') i++; i++; continue; }
            // Skip line comments
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            // Skip block comments
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++; i += 2; continue; }

            // Match BEGIN keyword (word boundary check)
            if (IsKeywordAt(text, i, "BEGIN"))
            {
                stack.Push(i);
                i += 5;
                continue;
            }

            // Match END keyword
            if (IsKeywordAt(text, i, "END"))
            {
                if (stack.Count > 0)
                {
                    var startOffset = stack.Pop();
                    foldings.Add(new NewFolding(startOffset, i + 3) { Name = "BEGIN...END" });
                }
                i += 3;
                continue;
            }

            i++;
        }
    }

    private static void FoldBlockComments(string text, List<NewFolding> foldings)
    {
        var i = 0;
        while (i < text.Length - 1)
        {
            if (text[i] == '/' && text[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++;
                if (i < text.Length - 1)
                {
                    i += 2;
                    // Only fold if multi-line
                    if (text[start..i].Contains('\n'))
                        foldings.Add(new NewFolding(start, i) { Name = "/* ... */" });
                }
            }
            else i++;
        }
    }

    private static void FoldCreateBlocks(string text, List<NewFolding> foldings)
    {
        // Find CREATE ... AS patterns and fold to the end of the object
        // This is a rough heuristic — fold from AS to the last END or GO
        // Skip for now — BEGIN/END folding already covers procedure bodies
    }

    private static bool IsKeywordAt(string text, int pos, string keyword)
    {
        if (pos + keyword.Length > text.Length) return false;
        // Check word boundary before
        if (pos > 0 && char.IsLetterOrDigit(text[pos - 1])) return false;
        // Check keyword match (case-insensitive)
        if (!text.AsSpan(pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        // Check word boundary after
        var after = pos + keyword.Length;
        if (after < text.Length && char.IsLetterOrDigit(text[after])) return false;
        return true;
    }
}
```

**Step 2: Wire into editor in `QueryTabView.axaml.cs` → `ConfigureEditor()`:**

```csharp
using AvaloniaEdit.Folding;

// In ConfigureEditor():
var foldingManager = FoldingManager.Install(SqlEditor.TextArea);
var foldingStrategy = new SqlFoldingStrategy();

// Update foldings on text change (debounced)
Timer? foldingTimer = null;
SqlEditor.TextChanged += (_, _) =>
{
    foldingTimer?.Dispose();
    foldingTimer = new Timer(_ =>
    {
        Dispatcher.UIThread.Post(() =>
        {
            var foldings = foldingStrategy.CreateNewFoldings(SqlEditor.Document);
            foldingManager.UpdateFoldings(foldings, -1);
        });
    }, null, 500, Timeout.Infinite); // 500ms debounce
};
```

**Step 3:** Dispose the `FoldingManager` when the tab is closed — add it to the tab's cleanup.

### Notes
- The `FoldingManager` and folding visual style is handled by AvaloniaEdit. Check if theme colors need to be set for the fold margin — if so, add resources to both theme files.
- BEGIN/END matching should skip `BEGIN TRAN`/`BEGIN TRANSACTION` or handle them gracefully (they don't have a matching END in the same way).
- Start with BEGIN/END and block comments. CREATE...AS folding is a nice-to-have for later.

---

## 7. "New Query" on Database Right-Click in Object Explorer

**Priority:** LOW — small but expected UX.  
**Files:** `Views/QueryEditorHost.axaml.cs` → `ShowContextMenu()`

### What
Right-clicking a Database node in OE should show "New Query" which opens a new tab with that database pre-selected.

### Implementation

In `ShowContextMenu()`, add a case for `ObjectExplorerNodeType.Database`:

```csharp
case ObjectExplorerNodeType.Database:
    menu.Items.Add(CreateMenuItem("New Query", () =>
    {
        var connStr = _registry?.GetConnectionString(node.ConnectionId!);
        var managed = _registry?.GetById(node.ConnectionId!);
        if (connStr != null)
        {
            AddNewTab(connStr, managed?.Config);
            // Set the database on the new tab
            if (ActiveTabViewModel != null)
                ActiveTabViewModel.SelectedDatabase = node.DatabaseName;
        }
    }));
    menu.Items.Add(new Separator());
    menu.Items.Add(CreateMenuItem("Refresh", () => explorer.RefreshNode(node)));
    break;
```

Note: The `SelectedDatabase` assignment has the same race condition as discussed in IMPROVEMENTS.md #12 — use the same fix (pass desired database to `LoadDatabasesForTabAsync`).

---

## 8. Table Properties Panel (lightweight)

**Priority:** LOW — nice-to-have, enhances exploration.  
**Files:** `Services/DatabaseService.cs`, `ViewModels/ObjectExplorerViewModel.cs`, `Views/QueryEditorHost.axaml.cs`

### What
Right-click a Table → "Properties" shows a small dialog or inline panel with: row count, data size (MB), index size (MB), create date, last modified, column count, index count.

### Implementation

**Step 1: Add method to `DatabaseService.cs`:**

```csharp
public record TableProperties(
    long RowCount, double DataSizeMB, double IndexSizeMB,
    DateTime CreateDate, DateTime? ModifyDate,
    int ColumnCount, int IndexCount);

public async Task<TableProperties?> GetTablePropertiesAsync(
    string connectionString, string database, string schema, string tableName)
{
    var connStr = BuildConnectionString(connectionString, database);
    using var conn = new SqlConnection(connStr);
    await conn.OpenAsync();

    var sql = @"
        SELECT
            SUM(p.rows) AS RowCount,
            SUM(CASE WHEN a.type = 1 THEN a.total_pages END) * 8.0 / 1024 AS DataSizeMB,
            SUM(CASE WHEN a.type = 2 THEN a.total_pages END) * 8.0 / 1024 AS IndexSizeMB,
            t.create_date, t.modify_date,
            (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = t.object_id) AS ColumnCount,
            (SELECT COUNT(*) FROM sys.indexes i WHERE i.object_id = t.object_id AND i.index_id > 0) AS IndexCount
        FROM sys.tables t
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        LEFT JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
        LEFT JOIN sys.allocation_units a ON p.partition_id = a.container_id
        WHERE s.name = @schema AND t.name = @table
        GROUP BY t.object_id, t.create_date, t.modify_date";

    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@schema", schema);
    cmd.Parameters.AddWithValue("@table", tableName);

    using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return null;

    return new TableProperties(
        RowCount: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
        DataSizeMB: reader.IsDBNull(1) ? 0 : Math.Round(reader.GetDouble(1), 2),
        IndexSizeMB: reader.IsDBNull(2) ? 0 : Math.Round(reader.GetDouble(2), 2),
        CreateDate: reader.GetDateTime(3),
        ModifyDate: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        ColumnCount: reader.GetInt32(5),
        IndexCount: reader.GetInt32(6));
}
```

**Step 2: Add "Properties" to the Table context menu in `QueryEditorHost.axaml.cs` → `ShowContextMenu()`:**

```csharp
// Inside case ObjectExplorerNodeType.Table:
menu.Items.Add(new Separator());
menu.Items.Add(CreateMenuItem("Properties", () => _ = ShowTablePropertiesAsync(node)));
```

**Step 3: Show in a simple dialog:**

Use the existing `ConfirmDialog` pattern or create a lightweight read-only dialog. The simplest approach:

```csharp
private async Task ShowTablePropertiesAsync(ObjectExplorerNode node)
{
    var connStr = ResolveConnectionString(node);
    if (connStr == null || _db == null) return;

    var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
    var props = await _db.GetTablePropertiesAsync(connStr, node.DatabaseName, schema, node.Name);
    if (props == null) return;

    var text = $"""
        Table: [{schema}].[{node.Name}]
        Database: {node.DatabaseName}

        Rows:         {props.RowCount:N0}
        Data Size:    {props.DataSizeMB:F2} MB
        Index Size:   {props.IndexSizeMB:F2} MB
        Columns:      {props.ColumnCount}
        Indexes:      {props.IndexCount}
        Created:      {props.CreateDate:yyyy-MM-dd HH:mm}
        Modified:     {props.ModifyDate?.ToString("yyyy-MM-dd HH:mm") ?? "—"}
        """;

    var parent = TopLevel.GetTopLevel(this) as Window;
    if (parent == null) return;
    var dialog = new ConfirmDialog(text, "OK");
    await dialog.ShowDialog(parent);
}
```

---

## Implementation Order

1. **Table row counts in OE** (#1) — fastest win, most visible
2. **Cell detail viewer** (#2) — biggest daily impact
3. **Results grid context menu** (#3) — high perceived value
4. **Bracket matching** (#5) — small, independent
5. **"New Query" on DB right-click** (#7) — tiny, do alongside #1
6. **Code folding** (#6) — medium effort, test carefully
7. **Export as JSON** (#4) — extend existing code
8. **Table properties** (#8) — nice-to-have, lowest priority


---

## 9. Object Explorer Visual Redesign

**Priority:** HIGH — the OE is the most visible part of the app and currently looks like an alpha build compared to the rest of the UI.  
**Files:** `Models/ObjectExplorerNode.cs`, `Views/QueryEditorHost.axaml`, `ViewModels/ObjectExplorerViewModel.cs`, `Services/DatabaseService.cs`, `Styles/AppTheme.axaml`, `Styles/AppThemeLight.axaml`

### Problem
Everything in the OE looks the same. Connection nodes, database nodes, folder nodes, and object nodes are all rendered as "tiny colored dot + text" at similar sizes and weights. At a glance you can't distinguish a table from a proc from a folder. The category separators are at 0.2 opacity (invisible in dark theme). Folders without children show no count, so you can't tell if they're worth expanding.

### Design: Colored Letter Badges (DataGrip/DBeaver style)

Replace the dot characters with 18×18px rounded-rect badges containing a single character. Keep the existing color palette — just apply it to a shape that's visually distinctive.

#### Badge assignments

| Node Type | Letter | Color (existing palette) | Background |
|-----------|--------|--------------------------|------------|
| Connection | `S` (server) | Connection's own color (e.g. `#e74c3c` for PROD) | Solid fill matching the env color |
| Database | cylinder SVG | `#888888` | `rgba(255,255,255,0.08)` |
| Tables folder | `T` | `#4caf7a` (green) | `rgba(42,110,78,0.15)` |
| Table (object) | `T` | `#3d8b5e` (dimmer green) | `rgba(42,110,78,0.08)` |
| Views folder | `V` | `#5ba3d9` (blue) | `rgba(41,128,185,0.15)` |
| View (object) | `V` | `#4a8fc0` (dimmer blue) | `rgba(41,128,185,0.08)` |
| Stored Procedures folder | `P` | `#b07cc7` (purple) | `rgba(142,68,173,0.15)` |
| Proc (object) | `P` | `#9a6aaf` (dimmer purple) | `rgba(142,68,173,0.08)` |
| Functions folder | `ƒ` (italic) | `#e6a04e` (amber) | `rgba(230,126,34,0.15)` |
| Function (object) | `ƒ` (italic) | `#cc8a3a` (dimmer amber) | `rgba(230,126,34,0.08)` |
| Sequences folder | `#` | `#2dbf9a` (teal) | `rgba(22,160,133,0.12)` |
| Sequence (object) | `#` | `#20a080` (dimmer teal) | `rgba(22,160,133,0.08)` |
| Types folder | `Ω` | `#2dbf9a` (teal) | `rgba(22,160,133,0.12)` |
| Database Triggers folder | `⚡` | `#e8723a` (orange) | `rgba(211,84,0,0.12)` |
| Trigger (object) | `⚡` | `#cc5f2e` (dimmer orange) | `rgba(211,84,0,0.08)` |
| Jobs folder | `J` | `#e8645a` (red) | `rgba(231,76,60,0.12)` |
| Job (object) | `J` | `#cc4f45` (dimmer red) | `rgba(231,76,60,0.08)` |
| Column (regular) | — (keep existing `○`) | `#888888` | none |
| Column (PK) | — (keep existing `●`) | `#f1c40f` | none |
| Parameter | — (keep existing `◇`) | `#9b59b6` | none |
| Index/FK/Constraint | — (keep existing info display) | `#888888` | none |

**Folder badges are bolder** (0.15 bg opacity), **object badges are subtler** (0.08 bg opacity). This creates visual hierarchy — you can scan folder headers quickly.

#### Light theme adjustments
The badge backgrounds need different opacities for light theme. In `AppThemeLight.axaml`, the tinted backgrounds should be slightly stronger since they're on a light surface:
- Folder badges: `rgba(color, 0.12)` 
- Object badges: `rgba(color, 0.06)`
- Badge text colors should be darker stops of the same hue (e.g., `#1a5c3a` instead of `#4caf7a` for table green)

### Implementation

#### Step 1: Update `ObjectExplorerNode.cs`

Replace the `Icon` property with a `BadgeLetter` and `BadgeBackground` property:

```csharp
public string BadgeLetter => NodeType switch
{
    ObjectExplorerNodeType.Connection => "S",
    ObjectExplorerNodeType.Table => "T",
    ObjectExplorerNodeType.View => "V",
    ObjectExplorerNodeType.Proc => "P",
    ObjectExplorerNodeType.Function => "ƒ",
    ObjectExplorerNodeType.Sequence => "#",
    ObjectExplorerNodeType.Job => "J",
    ObjectExplorerNodeType.Trigger => "⚡",
    ObjectExplorerNodeType.Column when IsPrimaryKey => "●",
    ObjectExplorerNodeType.Column => "○",
    ObjectExplorerNodeType.Parameter => "◇",
    ObjectExplorerNodeType.Folder when IsCategoryFolder => FolderBadgeLetter,
    _ => ""
};

private string FolderBadgeLetter => Name switch
{
    "Tables" => "T",
    "Views" => "V",
    "Stored Procedures" => "P",
    "Functions" => "ƒ",
    "Sequences" => "#",
    "Types" => "Ω",
    "Database Triggers" => "⚡",
    "Jobs" => "J",
    "Columns" => "",
    "Parameters" => "",
    "Indexes" => "",
    "Keys" => "",
    "Constraints" => "",
    "Triggers" => "⚡",
    _ => ""
};

/// <summary>Whether this node should render as a badge (rounded rect) vs inline text character.</summary>
public bool HasBadge => NodeType is ObjectExplorerNodeType.Connection
    or ObjectExplorerNodeType.Database
    or ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
    or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
    or ObjectExplorerNodeType.Sequence or ObjectExplorerNodeType.Job
    or ObjectExplorerNodeType.Trigger
    || (NodeType == ObjectExplorerNodeType.Folder && IsCategoryFolder && !string.IsNullOrEmpty(FolderBadgeLetter));

/// <summary>Whether this node is a category folder (bolder badge) vs a leaf object (subtler badge).</summary>
public bool IsFolderBadge => NodeType == ObjectExplorerNodeType.Folder && IsCategoryFolder;
```

Keep the existing `IconColor` property — it drives the badge text color. Add a `BadgeBackgroundColor` property that returns a color with appropriate opacity:

```csharp
/// <summary>Background color for the badge (tinted version of IconColor at low opacity).</summary>
public string BadgeBackground => NodeType switch
{
    ObjectExplorerNodeType.Connection => ConnectionColor ?? "#88a1bb",
    ObjectExplorerNodeType.Database => "rgba(255,255,255,0.08)",
    ObjectExplorerNodeType.Folder when IsCategoryFolder => $"0.15", // Opacity marker — converter handles it
    _ => $"0.08" // Object-level badges are subtler
};
```

**Alternative (simpler):** Instead of computing RGBA in the model, add a `BadgeOpacity` property (0.15 for folders, 0.08 for objects) and let the AXAML template handle the tinting using the existing `IconColor` + opacity. This avoids duplicating color logic.

```csharp
public double BadgeOpacity => (NodeType == ObjectExplorerNodeType.Folder && IsCategoryFolder) ? 0.15
    : NodeType == ObjectExplorerNodeType.Connection ? 1.0
    : 0.08;
```

#### Step 2: Update `QueryEditorHost.axaml` TreeDataTemplate

Replace the current icon display with a badge template. The key change is in the `TreeView.ItemTemplate`:

```xml
<TreeDataTemplate ItemsSource="{Binding Children}" x:DataType="models:ObjectExplorerNode">
    <StackPanel>
        <Border Height="1" Background="{DynamicResource TreeItemSeparator}"
                Opacity="0.4" Margin="0,3,0,1"
                IsVisible="{Binding IsCategoryFolder}"/>
        <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,1">
            <!-- Badge or icon -->
            <Panel Grid.Column="0" Width="20" Height="18" Margin="0,0,6,0">
                <!-- Colored letter badge (tables, views, procs, etc.) -->
                <Border IsVisible="{Binding HasBadge}"
                        Width="18" Height="18" CornerRadius="4"
                        HorizontalAlignment="Center" VerticalAlignment="Center">
                    <Border.Background>
                        <!-- For Connection nodes: solid fill. For others: tinted IconColor -->
                        <!-- Use a converter or code-behind to set this -->
                    </Border.Background>
                    <TextBlock Text="{Binding BadgeLetter}"
                               FontSize="10" FontWeight="600"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="{Binding IconColor}"/>
                </Border>
                <!-- Small character icon (columns, parameters — no badge bg) -->
                <TextBlock IsVisible="{Binding !HasBadge}"
                           Text="{Binding BadgeLetter}" FontSize="10"
                           Foreground="{Binding IconColor}"
                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Panel>

            <!-- Text content (middle, takes remaining space) -->
            <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6"
                        VerticalAlignment="Center">
                <!-- Object/folder name -->
                <TextBlock Text="{Binding DisplayName}"
                           FontSize="{Binding DisplayFontSize}"
                           FontWeight="{Binding IsBold, Converter={StaticResource BoolToFontWeight}}"
                           IsVisible="{Binding !IsColumn}"
                           VerticalAlignment="Center"/>
                <!-- Column: name + type info + PK + nullable (existing layout) -->
                <TextBlock Text="{Binding Name}" FontSize="12"
                           IsVisible="{Binding IsColumn}"
                           VerticalAlignment="Center"/>
                <TextBlock Text="{Binding TypeInfo}" FontSize="11"
                           Foreground="{DynamicResource ColumnTypeForeground}"
                           IsVisible="{Binding HasTypeInfo}"
                           VerticalAlignment="Center"/>
                <TextBlock Text="PK" FontSize="11" FontWeight="Bold"
                           Foreground="{DynamicResource ColumnPKForeground}"
                           IsVisible="{Binding ShowPK}"
                           VerticalAlignment="Center"/>
                <TextBlock Text="{Binding NullabilityText}" FontSize="11"
                           Foreground="{DynamicResource TextSecondary}"
                           IsVisible="{Binding ShowNullability}"
                           VerticalAlignment="Center"/>
                <TextBlock Text="Loading..." FontSize="11" FontStyle="Italic"
                           Foreground="{DynamicResource TextSecondary}"
                           IsVisible="{Binding IsLoading}"
                           VerticalAlignment="Center"/>
            </StackPanel>

            <!-- Right-aligned row count (tables only) -->
            <TextBlock Grid.Column="2" 
                       Text="{Binding FormattedRowCount}"
                       FontSize="10.5"
                       Foreground="{DynamicResource TextDisabled}"
                       VerticalAlignment="Center"
                       Margin="4,0,4,0"
                       IsVisible="{Binding ShowRowCount}"/>
        </Grid>
    </StackPanel>
</TreeDataTemplate>
```

#### Step 3: Badge background converter

The badge background needs to be a tinted version of `IconColor`. Create a simple converter `BadgeBackgroundConverter` in `Converters/`:

```csharp
public class BadgeBackgroundConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return Brushes.Transparent;
        
        var hexColor = values[0] as string ?? "#888888";
        var opacity = values[1] is double d ? d : 0.08;
        
        try
        {
            var color = Color.Parse(hexColor);
            var alpha = (byte)(opacity * 255);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }
        catch
        {
            return Brushes.Transparent;
        }
    }
}
```

Usage in AXAML:
```xml
<Border.Background>
    <MultiBinding Converter="{StaticResource BadgeBackgroundConverter}">
        <Binding Path="IconColor"/>
        <Binding Path="BadgeOpacity"/>
    </MultiBinding>
</Border.Background>
```

Register the converter in `App.axaml` or the local resources of `QueryEditorHost.axaml`.

**Special case — Connection node:** The badge background should be the solid connection color (opacity 1.0), with white text. Check `NodeType == Connection` and use white foreground for the badge letter instead of `IconColor`.

**Special case — Database node:** Instead of a letter badge, use a tiny inline SVG cylinder. The simplest approach: use a `PathIcon` or just the letter `D` in the same badge style. A proper cylinder SVG as a `DrawingImage` resource is also fine but more work.

#### Step 4: Folder counts for all categories

Currently only the Tables folder shows a count (from `ChildCount` after expanding). Change this: when a **database node expands** and creates its category folders, fire a single query to get counts for all object types:

**Add to `DatabaseService.cs`:**

```csharp
public async Task<Dictionary<string, int>> GetObjectCountsAsync(string connectionString, string database)
{
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var connStr = BuildConnectionString(connectionString, database);
    using var conn = new SqlConnection(connStr);
    await conn.OpenAsync();

    var sql = @"
        SELECT 
            CASE o.type
                WHEN 'U'  THEN 'Tables'
                WHEN 'V'  THEN 'Views'
                WHEN 'P'  THEN 'Stored Procedures'
                WHEN 'FN' THEN 'Functions'
                WHEN 'IF' THEN 'Functions'
                WHEN 'TF' THEN 'Functions'
                WHEN 'TR' THEN 'Triggers'
            END AS Category,
            COUNT(*) AS Cnt
        FROM sys.objects o
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','FN','IF','TF','TR')
        GROUP BY CASE o.type
                WHEN 'U'  THEN 'Tables'
                WHEN 'V'  THEN 'Views'
                WHEN 'P'  THEN 'Stored Procedures'
                WHEN 'FN' THEN 'Functions'
                WHEN 'IF' THEN 'Functions'
                WHEN 'TF' THEN 'Functions'
                WHEN 'TR' THEN 'Triggers'
            END";

    using var cmd = new SqlCommand(sql, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var cat = reader.GetString(0);
        var cnt = reader.GetInt32(1);
        if (result.ContainsKey(cat))
            result[cat] += cnt; // Functions groups multiple types
        else
            result[cat] = cnt;
    }

    // Also get sequences count
    try
    {
        using var seqCmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.sequences WHERE is_ms_shipped = 0", conn);
        result["Sequences"] = (int)(await seqCmd.ExecuteScalarAsync() ?? 0);
    }
    catch { /* Sequences not available on older SQL Server */ }

    // Jobs count (from msdb)
    try
    {
        using var jobCmd = new SqlCommand(
            "SELECT COUNT(*) FROM msdb.dbo.sysjobs", conn);
        result["Jobs"] = (int)(await jobCmd.ExecuteScalarAsync() ?? 0);
    }
    catch { /* msdb access might be restricted */ }

    // User-defined types
    try
    {
        using var typeCmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1", conn);
        result["Types"] = (int)(await typeCmd.ExecuteScalarAsync() ?? 0);
    }
    catch { }

    // Database triggers
    try
    {
        using var dtCmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0", conn);
        result["Database Triggers"] = (int)(await dtCmd.ExecuteScalarAsync() ?? 0);
    }
    catch { }

    return result;
}
```

**Update `LoadDatabaseChildrenAsync` in `ObjectExplorerViewModel.cs`:**

After creating the folder nodes, fire-and-forget the counts:

```csharp
private async Task LoadDatabaseChildrenAsync(ObjectExplorerNode dbNode)
{
    // ... existing folder creation code ...

    // Fire-and-forget: fetch object counts for all category folders
    _ = Task.Run(async () =>
    {
        try
        {
            var connStr = ResolveConnectionString(dbNode);
            if (connStr == null) return;
            var counts = await _db.GetObjectCountsAsync(connStr, dbNode.DatabaseName);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var folder in dbNode.Children)
                {
                    if (counts.TryGetValue(folder.Name, out var count))
                        folder.ChildCount = count;
                    else if (folder.IsCategoryFolder)
                        folder.ChildCount = 0; // Explicitly show (0)
                }
            });
        }
        catch { /* best effort */ }
    });

    return;
}
```

#### Step 5: Connection node server name split

Currently the connection node shows `PROD TestDB (localhost)` as one string. Split it so the connection name is prominent and the server hostname is dimmed:

Add properties to `ObjectExplorerNode.cs`:

```csharp
public string ConnectionDisplayName
{
    get
    {
        if (NodeType != ObjectExplorerNodeType.Connection) return "";
        // Name is currently "DisplayName (server)" — split it
        var parenIdx = Name.LastIndexOf('(');
        return parenIdx > 0 ? Name[..parenIdx].Trim() : Name;
    }
}

public string ConnectionServerHint
{
    get
    {
        if (NodeType != ObjectExplorerNodeType.Connection) return "";
        var parenIdx = Name.LastIndexOf('(');
        return parenIdx > 0 ? Name[parenIdx..].Trim('(', ')', ' ') : "";
    }
}
```

In the AXAML template, for connection nodes show both parts:
```xml
<StackPanel Orientation="Horizontal" Spacing="5" IsVisible="{Binding IsConnectionNode}">
    <TextBlock Text="{Binding ConnectionDisplayName}" FontSize="13" FontWeight="600"/>
    <TextBlock Text="{Binding ConnectionServerHint}" FontSize="10.5"
               Foreground="{DynamicResource TextDisabled}" VerticalAlignment="Center"/>
</StackPanel>
```

#### Step 6: Category separator visibility

Update the separator opacity from 0.2 to 0.4 and add a top margin:

In the TreeDataTemplate, the separator Border:
```xml
<Border Height="1" Background="{DynamicResource TreeItemSeparator}"
        Opacity="0.4" Margin="0,3,0,1"
        IsVisible="{Binding IsCategoryFolder}"/>
```

### Row count display on table objects

This ties into feature #1 (table row counts). Add display properties to `ObjectExplorerNode.cs`:

```csharp
public bool ShowRowCount => NodeType == ObjectExplorerNodeType.Table && RowCount >= 0;

public string FormattedRowCount => RowCount switch
{
    < 0 => "",
    < 1_000 => RowCount.ToString(),
    < 1_000_000 => $"{RowCount / 1_000.0:F1}K",
    < 1_000_000_000 => $"{RowCount / 1_000_000.0:F1}M",
    _ => $"{RowCount / 1_000_000_000.0:F1}B"
};
```

These are right-aligned in the tree row (see Grid Column 2 in the template above).

### Summary of visual changes

1. **Dots → Letter badges** (18×18 rounded rects with tinted backgrounds)
2. **Folder badges bolder, object badges subtler** (opacity difference creates hierarchy)
3. **Connection node** gets a solid-color badge matching env, server name dimmed
4. **Database node** gets a subtle cylinder icon or `D` badge
5. **All category folders show counts** immediately (single metadata query on expand)
6. **Table objects show row counts** right-aligned (from `sys.dm_db_partition_stats`)
7. **Category separators** more visible (0.4 opacity, 3px top margin)
8. **Columns/Parameters/Indexes** keep existing display (no badge, just character + text)
