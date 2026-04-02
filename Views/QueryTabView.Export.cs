using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    /// <summary>Fired when "Filter by Value" is clicked — host should open a new tab.</summary>
    public event Action<string>? FilterByValueRequested;

    private CancellationTokenSource? _exportCts;

    private void OnExportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var menu = new MenuFlyout();

        // In stacked mode with multiple results, add a result picker sub-menu
        if (_isStackedMode && _viewModel.Results.Count(r => r.Error == null) > 1)
        {
            var goodResults = _viewModel.Results.Where(r => r.Error == null).ToList();

            foreach (var format in new[] { ("xlsx", "Excel (.xlsx)"), ("csv", "CSV"), ("json", "JSON"), ("tsv", "Tab-Delimited") })
            {
                var formatItem = new MenuItem { Header = $"Export as {format.Item2}" };
                for (int i = 0; i < goodResults.Count; i++)
                {
                    var resultIdx = _viewModel.Results.IndexOf(goodResults[i]);
                    var capturedIdx = resultIdx;
                    var subItem = new MenuItem { Header = $"Result {i + 1} ({goodResults[i].RowCount} rows)" };
                    var fmt = format.Item1;
                    subItem.Click += async (_, _) => await ExportResultsAsync(fmt, capturedIdx);
                    formatItem.Items.Add(subItem);
                }
                menu.Items.Add(formatItem);
            }
        }
        else
        {
            var excelItem = new MenuItem { Header = "Export as Excel (.xlsx)" };
            excelItem.Click += async (_, _) => await ExportResultsAsync("xlsx");
            menu.Items.Add(excelItem);

            var csvItem = new MenuItem { Header = "Export as CSV" };
            csvItem.Click += async (_, _) => await ExportResultsAsync("csv");
            menu.Items.Add(csvItem);

            var jsonItem = new MenuItem { Header = "Export as JSON" };
            jsonItem.Click += async (_, _) => await ExportResultsAsync("json");
            menu.Items.Add(jsonItem);

            var tsvItem = new MenuItem { Header = "Export as Tab-Delimited" };
            tsvItem.Click += async (_, _) => await ExportResultsAsync("tsv");
            menu.Items.Add(tsvItem);
        }

        menu.ShowAt(ExportButton, true);
    }

    private void WireExportCancelButton()
    {
        ExportCancelButton.Click += (_, _) =>
        {
            _exportCts?.Cancel();
        };
    }

    private void ShowExportProgress(bool visible)
    {
        ExportProgressPanel.IsVisible = visible;
        ExportButton.IsEnabled = !visible;
        if (visible)
        {
            ExportProgressBar.Value = 0;
            ExportProgressText.Text = "0%";
        }
    }

    private async void FlashExportButton(bool success)
    {
        var color = success
            ? Avalonia.Media.Color.FromRgb(39, 174, 96)   // green
            : Avalonia.Media.Color.FromRgb(231, 76, 60);  // red
        var flashBrush = new Avalonia.Media.SolidColorBrush(color);
        var originalContent = ExportButton.Content;

        ExportButton.Foreground = flashBrush;
        ExportButton.Content = success ? "\u2713 Export" : "\u2717 Export";

        await Task.Delay(1500);

        ExportButton.Content = originalContent;
        ExportButton.Foreground = GetRowBrush("TextSecondary");
    }

    private void UpdateExportProgress(int current, int total)
    {
        var pct = total > 0 ? (int)(current * 100.0 / total) : 0;
        ExportProgressBar.Value = pct;
        ExportProgressText.Text = $"{pct}%";
    }

    private async Task ExportResultsAsync(string format, int? overrideResultIndex = null)
    {
        if (_viewModel == null) return;

        int resultIndex;
        if (overrideResultIndex.HasValue)
        {
            resultIndex = overrideResultIndex.Value;
        }
        else
        {
            resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
                ? _selectedTabIndex : 0;
        }
        if (resultIndex >= _viewModel.Results.Count) return;

        var result = _viewModel.Results[resultIndex];
        if (result.Error != null) return;

        var selectedRows = GetSelectedRows();
        var rowsToExport = selectedRows.Count > 0 ? selectedRows : result.Rows;
        var isPartial = selectedRows.Count > 0;

        var (extension, description) = format switch
        {
            "xlsx" => ("xlsx", "Excel Files"),
            "csv" => ("csv", "CSV Files"),
            "json" => ("json", "JSON Files"),
            "tsv" => ("tsv", "Tab-Delimited Files"),
            _ => ("xlsx", "Excel Files")
        };

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = $"Export as {extension.ToUpperInvariant()}",
                SuggestedFileName = "results",
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }]
            });

        var path = file?.TryGetLocalPath();
        if (path == null) return;

        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;
        ShowExportProgress(true);

        try
        {
            switch (format)
            {
                case "xlsx":
                    await Task.Run(() => ExportService.ExportToExcel(result.ColumnNames, result.ColumnTypes, rowsToExport, path), ct);
                    break;
                case "csv":
                    await WriteDelimitedAsync(result, rowsToExport, ",", path, ct);
                    break;
                case "tsv":
                    await WriteDelimitedAsync(result, rowsToExport, "\t", path, ct);
                    break;
                case "json":
                    await WriteJsonAsync(result, rowsToExport, path, ct);
                    break;
            }

            _viewModel.StatusText = isPartial
                ? $"Exported {rowsToExport.Count:N0} of {result.RowCount:N0} rows to {Path.GetFileName(path)}"
                : $"Exported {result.RowCount:N0} rows to {Path.GetFileName(path)}";
            FlashExportButton(success: true);
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Export cancelled";
            try { File.Delete(path); } catch (Exception ex) { AppLogger.LogError("Export.CleanupCancelled", ex); }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Export failed: {ex.Message}";
            FlashExportButton(success: false);
        }
        finally
        {
            ShowExportProgress(false);
            _exportCts = null;
        }
    }

    private async Task WriteDelimitedAsync(QueryResult result, List<object?[]> rows, string delimiter, string path, CancellationToken ct)
    {
        var total = rows.Count;
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteLineAsync(string.Join(delimiter, result.ColumnNames.Select(c =>
            delimiter == "," ? $"\"{c.Replace("\"", "\"\"")}\"" : c)));

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            await writer.WriteLineAsync(string.Join(delimiter, row.Select(v =>
            {
                if (v == null || v == DBNull.Value) return "";
                var text = v.ToString() ?? "";
                return delimiter == "," ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
            })));

            if (i % 500 == 0)
            {
                var captured = i;
                Dispatcher.UIThread.Post(() => UpdateExportProgress(captured, total));
                await Task.Yield();
            }
        }
        Dispatcher.UIThread.Post(() => UpdateExportProgress(total, total));
    }

    private async Task WriteJsonAsync(QueryResult result, List<object?[]> rows, string path, CancellationToken ct)
    {
        var total = rows.Count;
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteLineAsync("[");

        for (int r = 0; r < total; r++)
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder("  {");
            for (int c = 0; c < result.ColumnNames.Length; c++)
            {
                if (c > 0) sb.Append(", ");
                var val = c < rows[r].Length ? rows[r][c] : null;
                sb.Append($"\"{EscapeJsonString(result.ColumnNames[c])}\": ");
                if (val == null || val == DBNull.Value)
                    sb.Append("null");
                else if (val is bool b)
                    sb.Append(b ? "true" : "false");
                else if (IsNumericType(val.GetType()))
                    sb.Append(val);
                else
                    sb.Append($"\"{EscapeJsonString(val.ToString() ?? "")}\"");
            }
            sb.Append(r < total - 1 ? "}," : "}");
            await writer.WriteLineAsync(sb.ToString());

            if (r % 500 == 0)
            {
                var captured = r;
                Dispatcher.UIThread.Post(() => UpdateExportProgress(captured, total));
                await Task.Yield();
            }
        }
        await writer.WriteLineAsync("]");
        Dispatcher.UIThread.Post(() => UpdateExportProgress(total, total));
    }

    // Keep for small inline uses (Copy as INSERT, etc.)
    private static string ResultToDelimited(QueryResult result, List<object?[]> rows, string delimiter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, result.ColumnNames.Select(c =>
            delimiter == "," ? $"\"{c.Replace("\"", "\"\"")}\"" : c)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(delimiter, row.Select(v =>
            {
                if (v == null || v == DBNull.Value) return "";
                var text = v.ToString() ?? "";
                return delimiter == "," ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
            })));
        }
        return sb.ToString();
    }

    private static string ResultToJson(QueryResult result, List<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append("  {");
            for (int c = 0; c < result.ColumnNames.Length; c++)
            {
                if (c > 0) sb.Append(", ");
                var val = c < rows[r].Length ? rows[r][c] : null;
                sb.Append($"\"{EscapeJsonString(result.ColumnNames[c])}\": ");
                if (val == null || val == DBNull.Value)
                    sb.Append("null");
                else if (val is bool b)
                    sb.Append(b ? "true" : "false");
                else if (IsNumericType(val.GetType()))
                    sb.Append(val);
                else
                    sb.Append($"\"{EscapeJsonString(val.ToString() ?? "")}\"");
            }
            sb.Append(r < rows.Count - 1 ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("]");
        return sb.ToString();
    }

    private static string EscapeJsonString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>Get the currently displayed result (live or pinned).</summary>
    private QueryResult? GetCurrentResult()
    {
        if (_viewModel == null) return null;

        // Stacked mode: find which result the active grid is showing
        if (_isStackedMode && _activeResultsGrid != ResultsGrid)
        {
            var grids = StackedResultsPanel.GetVisualDescendants().OfType<DataGrid>().ToList();
            var gridIdx = grids.IndexOf(_activeResultsGrid);
            var goodResults = _viewModel.Results.Where(r => r.Error == null).ToList();
            if (gridIdx >= 0 && gridIdx < goodResults.Count)
                return goodResults[gridIdx];
        }

        if (_selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count)
            return _viewModel.Results[_selectedTabIndex];
        if (_selectedTabIndex < 0 && _selectedTabIndex != MessagesTabTag)
        {
            var pinnedIdx = -(_selectedTabIndex + 1);
            if (pinnedIdx >= 0 && pinnedIdx < _pinnedResults.Count)
                return _pinnedResults[pinnedIdx].Result;
        }
        return _viewModel.Results.Count > 0 ? _viewModel.Results[0] : null;
    }

    private async Task CopyWithHeadersAsync()
    {
        var result = GetCurrentResult();
        if (result == null) return;

        var selected = GetSelectedRows();
        var rows = selected.Count > 0 ? selected : result.Rows;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", result.ColumnNames));
        foreach (var row in rows)
            sb.AppendLine(string.Join("\t", row.Select(v => v?.ToString() ?? "NULL")));

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(sb.ToString());
            var label = selected.Count > 0 ? $"{selected.Count} selected row" : $"{rows.Count} row";
            if (rows.Count != 1) label += "s";
            _viewModel.Flash($"\u2713 {label} copied", ViewModels.QueryStatusSeverity.Success);
        }
    }

    private List<object?[]> GetSelectedRows()
    {
        var grid = _activeResultsGrid;
        var list = new List<object?[]>();
        foreach (var item in grid.SelectedItems)
        {
            if (item is object?[] row) list.Add(row);
        }
        return list;
    }

    private async Task<string?> ShowExcelSaveDialog()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export to Excel",
                SuggestedFileName = "results",
                DefaultExtension = "xlsx",
                FileTypeChoices =
                [
                    new FilePickerFileType("Excel Files") { Patterns = ["*.xlsx"] },
                ]
            });

        return file?.TryGetLocalPath();
    }

    // ── Read-Only Context Menu ─────────────────────────────────────────

    private void SetupReadOnlyContextMenu()
    {
        var menu = new ContextMenu();

        var copyCellValue = new MenuItem { Header = "Copy Cell Value" };
        copyCellValue.Click += async (_, _) => await CopyCellValueAsync();

        var copyRow = new MenuItem { Header = "Copy Row" };
        copyRow.Click += async (_, _) => await CopySelectedRowsAsync();

        var copyWithHeaders = new MenuItem { Header = "Copy with Headers" };
        copyWithHeaders.Click += async (_, _) => await CopyWithHeadersAsync();

        var copyInsert = new MenuItem { Header = "Copy as INSERT" };
        copyInsert.Click += async (_, _) => await CopyAsInsertAsync();

        var copyAllInsert = new MenuItem { Header = "Copy All as INSERT" };
        copyAllInsert.Click += async (_, _) => await CopyAllAsInsertAsync();

        var filterByValue = new MenuItem { Header = "Filter by This Value" };
        filterByValue.Click += (_, _) => FilterByCurrentCellValue();

        var separator1 = new Separator();

        var exportSelected = new MenuItem { Header = "Export Selected to Excel" };
        exportSelected.Click += async (_, _) =>
        {
            var rows = GetSelectedRows();
            if (rows.Count == 0 || _viewModel == null) return;

            var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
                ? _selectedTabIndex : 0;
            var result = _viewModel.Results[resultIndex];

            var path = await ShowExcelSaveDialog();
            if (path == null) return;

            try
            {
                ExportService.ExportToExcel(result.ColumnNames, result.ColumnTypes, rows, path);
                _viewModel.StatusText = $"Exported {rows.Count:N0} of {result.RowCount:N0} rows to {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = $"Export failed: {ex.Message}";
            }
        };

        menu.Items.Add(copyCellValue);
        menu.Items.Add(copyRow);
        menu.Items.Add(copyWithHeaders);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyInsert);
        menu.Items.Add(copyAllInsert);
        menu.Items.Add(separator1);
        menu.Items.Add(filterByValue);
        menu.Items.Add(new Separator());
        menu.Items.Add(exportSelected);

        menu.Opening += (_, _) =>
        {
            var hasSelection = ResultsGrid.SelectedItems.Count > 0;
            var hasTable = _viewModel?.EditTableSchema != null && _viewModel?.EditTableName != null;
            var result = GetCurrentResult();

            copyCellValue.IsVisible = hasSelection;
            copyRow.IsVisible = hasSelection;
            copyInsert.IsEnabled = hasSelection && hasTable;
            copyAllInsert.IsVisible = hasTable && result != null && result.Rows.Count <= 1000;
            if (result != null && hasTable)
                copyAllInsert.Header = $"Copy All as INSERT ({result.Rows.Count} rows)";

            // Filter by value: only when a cell is selected and has a non-null value
            var cellValue = GetCurrentCellValue();
            filterByValue.IsVisible = cellValue != null && cellValue != DBNull.Value;
            if (filterByValue.IsVisible)
            {
                var text = cellValue?.ToString() ?? "";
                filterByValue.Header = $"Filter by '{(text.Length > 30 ? text[..27] + "..." : text)}'";
            }

            exportSelected.IsVisible = hasSelection;
        };

        ResultsGrid.ContextMenu = menu;
    }

    private object? GetCurrentCellValue()
    {
        if (ResultsGrid.SelectedItem == null || ResultsGrid.CurrentColumn == null)
            return null;
        var colIndex = ResultsGrid.Columns.IndexOf(ResultsGrid.CurrentColumn);
        if (colIndex < 0) return null;
        if (ResultsGrid.SelectedItem is object?[] row && colIndex < row.Length)
            return row[colIndex];
        return null;
    }

    private async Task CopyCellValueAsync()
    {
        var cellValue = GetCurrentCellValue();
        var text = cellValue == null || cellValue == DBNull.Value ? "NULL" : cellValue.ToString() ?? "";
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            if (_viewModel != null) _viewModel.StatusText = "Cell value copied";
        }
    }

    private async Task CopyAllAsInsertAsync()
    {
        if (_viewModel == null) return;
        var result = GetCurrentResult();
        if (result == null || _viewModel.EditTableSchema == null || _viewModel.EditTableName == null) return;

        var sql = ExportService.GenerateInsertStatements(
            _viewModel.EditTableSchema, _viewModel.EditTableName,
            result.ColumnNames, result.ColumnTypes, result.Rows);

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(sql);
            _viewModel.StatusText = $"Copied {result.Rows.Count} INSERT statement{(result.Rows.Count == 1 ? "" : "s")}";
        }
    }

    private void FilterByCurrentCellValue()
    {
        var result = GetCurrentResult();
        if (result == null || ResultsGrid.CurrentColumn == null) return;

        var colIndex = ResultsGrid.Columns.IndexOf(ResultsGrid.CurrentColumn);
        if (colIndex < 0 || colIndex >= result.ColumnNames.Length) return;

        var cellValue = GetCurrentCellValue();
        if (cellValue == null || cellValue == DBNull.Value) return;

        var colName = result.ColumnNames[colIndex];
        var cellText = cellValue.ToString() ?? "";
        var isNumeric = colIndex < result.ColumnTypes.Length && IsNumericType(result.ColumnTypes[colIndex]);
        var whereClause = isNumeric
            ? $"WHERE [{colName}] = {cellText}"
            : $"WHERE [{colName}] = '{cellText.Replace("'", "''")}'";

        var sql = result.SourceSql != null
            ? $"-- Filter from results\nSELECT * FROM (\n{result.SourceSql}\n) sub\n{whereClause}"
            : $"-- TODO: add table name\nSELECT * FROM [???]\n{whereClause}";

        FilterByValueRequested?.Invoke(sql);
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    private async Task CopyAsInsertAsync()
    {
        try
        {
            if (_viewModel == null) return;
            var rows = GetSelectedRows();
            if (rows.Count == 0) { _viewModel.StatusText = "Select rows first"; return; }
            if (_viewModel.EditTableSchema == null || _viewModel.EditTableName == null)
            { _viewModel.StatusText = "Copy as INSERT requires a simple single-table SELECT"; return; }

            var result = GetCurrentResult();
            if (result == null) return;

            var sql = ExportService.GenerateInsertStatements(
                _viewModel.EditTableSchema, _viewModel.EditTableName,
                result.ColumnNames, result.ColumnTypes, rows);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(sql);
                _viewModel.StatusText = $"Copied {rows.Count} INSERT statement{(rows.Count == 1 ? "" : "s")}";
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryTabView.CopyAsInsert", ex);
            if (_viewModel != null)
                _viewModel.StatusText = $"Copy as INSERT failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task CopySelectedRowsAsync()
    {
        try
        {
            if (_viewModel == null) return;
            var rows = GetSelectedRows();
            if (rows.Count == 0) return;

            var result = GetCurrentResult();
            if (result == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", result.ColumnNames));
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join("\t", row.Select(v => v?.ToString() ?? "NULL")));
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(sb.ToString());
                _viewModel.StatusText = $"Copied {rows.Count} row{(rows.Count == 1 ? "" : "s")}";
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryTabView.CopySelectedRows", ex);
            if (_viewModel != null)
                _viewModel.StatusText = $"Copy rows failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
