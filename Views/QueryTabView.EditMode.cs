using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SqlVersionControl.Models;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    // ── Cell Detail Panel Resize ────────────────────────────────────
    private bool _cellDetailResizing;
    private bool _cellDetailEnabled; // toggle for cell detail panel (off by default)
    private Point _cellDetailResizeStart;
    private double _cellDetailStartHeight;

    private IBrush AlternateBrush => GetRowBrush("ResultsAlternateRow");

    private void OnEditModeChanged()
    {
        if (_viewModel == null) return;
        CellDetailPanel.IsVisible = false;

        var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
            ? _selectedTabIndex : 0;

        if (_viewModel.IsEditMode && _viewModel.EditableRows != null &&
            resultIndex < _viewModel.Results.Count)
        {
            // Enter edit mode: toggle columns to writable, swap ItemsSource (no column rebuild)
            SetColumnsReadOnly(false);
            ResultsGrid.IsReadOnly = false;
            ResultsGrid.CanUserSortColumns = false;
            ResultsGrid.ItemsSource = _viewModel.EditableRows;
            SetupEditContextMenu();
        }
        else
        {
            // Exit edit mode: toggle columns back to read-only, restore original rows
            SetColumnsReadOnly(true);
            ResultsGrid.IsReadOnly = true;
            ResultsGrid.CanUserSortColumns = true;
            if (resultIndex >= 0 && resultIndex < (_viewModel.Results?.Count ?? 0))
            {
                var result = _viewModel.Results[resultIndex];
                ResultsGrid.ItemsSource = result.Rows;
            }
            SetupReadOnlyContextMenu();
        }

        UpdateEditModeButton();
        UpdateEditBar();
    }

    private void OnCellDetailResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(CellDetailResizeHandle).Properties.IsLeftButtonPressed)
        {
            _cellDetailResizing = true;
            _cellDetailResizeStart = e.GetPosition(this);
            _cellDetailStartHeight = CellDetailPanel.Height;
            e.Pointer.Capture(CellDetailResizeHandle);
            e.Handled = true;
        }
    }

    private void OnCellDetailResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_cellDetailResizing) return;
        var current = e.GetPosition(this);
        var delta = _cellDetailResizeStart.Y - current.Y; // positive = dragged up = panel grows

        var resultsHeight = EditorResultsGrid.RowDefinitions[2].ActualHeight;
        var maxGrowth = resultsHeight - 80; // keep at least 80px for results grid
        var maxDetailHeight = _cellDetailStartHeight + Math.Max(maxGrowth, 0);
        var newHeight = Math.Clamp(_cellDetailStartHeight + delta, 40, maxDetailHeight);
        var actualDelta = newHeight - _cellDetailStartHeight;

        // Shrink results grid by the same amount the detail panel grows
        if (actualDelta != 0)
        {
            var newResultsHeight = Math.Max(resultsHeight - actualDelta, 80);
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(newResultsHeight, GridUnitType.Pixel);
        }

        CellDetailPanel.Height = newHeight;
        _cellDetailResizeStart = current;
        _cellDetailStartHeight = newHeight;
        e.Handled = true;
    }

    private void OnCellDetailResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_cellDetailResizing)
        {
            _cellDetailResizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnResultsGridCellSelected(object? sender, SelectionChangedEventArgs e) => UpdateCellDetail();

    private void UpdateCellDetail()
    {
        if (!_cellDetailEnabled)
        {
            CellDetailPanel.IsVisible = false;
            return;
        }

        var grid = _activeResultsGrid;
        if (grid.SelectedItem == null || grid.CurrentColumn == null)
        {
            CellDetailPanel.IsVisible = false;
            return;
        }

        var colIndex = grid.Columns.IndexOf(grid.CurrentColumn);
        if (colIndex < 0) { CellDetailPanel.IsVisible = false; return; }

        var colName = grid.CurrentColumn.Header?.ToString() ?? "";
        object? cellValue = null;

        if (grid.SelectedItem is object?[] row && colIndex < row.Length)
            cellValue = row[colIndex];
        else if (grid.SelectedItem is EditableRow editRow)
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

    private void UpdateCellDetailToggleButton()
    {
        CellDetailToggleButton.Opacity = _cellDetailEnabled ? 1.0 : 0.5;
    }

    private async void OnCopyMessageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Avalonia.Controls.SelectableTextBlock stb) return;
        if (stb.Tag is not SqlVersionControl.Models.QueryMessage msg) return;
        if (msg.Type != SqlVersionControl.Models.MessageType.Error || msg.LineNumber < 1) return;

        // Navigate to the error line in the editor
        SqlEditor.TextArea.Caret.Line = msg.LineNumber;
        SqlEditor.TextArea.Caret.Column = 1;
        SqlEditor.ScrollTo(msg.LineNumber, 1);
        SqlEditor.Focus();
    }

    private async void OnResultsGridDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.CanEditMode) return;

        if (_viewModel.IsEditMode)
        {
            // Already in edit mode — just begin editing the current cell
            ResultsGrid.BeginEdit();
            return;
        }

        // Capture the clicked row index and column before ItemsSource swap resets selection
        var clickedRowIndex = ResultsGrid.SelectedIndex;
        var clickedColumn = ResultsGrid.CurrentColumn;

        // Enter edit mode (no column rebuild — just toggles IsReadOnly + swaps ItemsSource)
        await _viewModel.ToggleEditModeCommand.ExecuteAsync(null);

        // Post to let ItemsSource swap settle, then restore selection and begin editing
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (clickedRowIndex >= 0 && clickedRowIndex < ResultsGrid.ItemsSource?.Cast<object>().Count())
            {
                ResultsGrid.SelectedIndex = clickedRowIndex;
                if (clickedColumn != null)
                    ResultsGrid.CurrentColumn = clickedColumn;
                ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem, clickedColumn);
            }
            ResultsGrid.BeginEdit();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async void OnResultsGridKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Copy with Headers: Cmd/Ctrl+Shift+C (works in both read-only and edit mode)
        if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            await CopyWithHeadersAsync();
            return;
        }

        // Cell copy: Cmd/Ctrl+C copies selected column(s) from all selected rows
        if (ctrl && !shift && e.Key == Key.C)
        {
            var grid = _activeResultsGrid;
            if (grid.SelectedItems != null && grid.SelectedItems.Count > 0)
            {
                // Determine column range
                int minCol, maxCol;
                if (_dragStartColIndex >= 0 && _dragEndColIndex >= 0)
                {
                    minCol = Math.Min(_dragStartColIndex, _dragEndColIndex);
                    maxCol = Math.Max(_dragStartColIndex, _dragEndColIndex);
                }
                else if (grid.CurrentColumn != null)
                {
                    minCol = maxCol = grid.Columns.IndexOf(grid.CurrentColumn);
                }
                else if (_fullRowSelectionMode)
                {
                    minCol = 0;
                    maxCol = grid.Columns.Count - 1;
                }
                else
                {
                    minCol = maxCol = -1;
                }

                if (minCol >= 0)
                {
                    var values = new System.Text.StringBuilder();
                    foreach (var item in grid.SelectedItems)
                    {
                        if (values.Length > 0) values.AppendLine();

                        for (int c = minCol; c <= maxCol; c++)
                        {
                            if (c > minCol) values.Append('\t');

                            object? cellValue = null;
                            if (item is object?[] row && c < row.Length)
                                cellValue = row[c];
                            else if (item is EditableRow editRow)
                                cellValue = editRow[c];

                            values.Append(cellValue == null || cellValue == DBNull.Value ? "" : cellValue.ToString() ?? "");
                        }
                    }

                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        e.Handled = true;
                        await clipboard.SetTextAsync(values.ToString());
                        return;
                    }
                }
            }
        }

        if (_viewModel is not { IsEditMode: true }) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            // If a cell TextBox is focused, first Escape just cancels the cell edit
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is Avalonia.Controls.TextBox tb && tb.FindAncestorOfType<DataGridCell>() != null)
            {
                ResultsGrid.CancelEdit();
                return;
            }

            // No cell being actively edited — exit edit mode
            bool hasChanges = _viewModel.EditableRows?.Any(r =>
                r.State != RowEditState.None) == true;

            if (hasChanges)
            {
                _ = PromptExitEditModeAsync();
            }
            else
            {
                _viewModel.CancelChangesCommand.Execute(null);
            }
            return;
        }

        if (ctrl && e.Key == Key.Z)
        {
            e.Handled = true; // Prevent bubbling to AvaloniaEdit's undo
            if (ResultsGrid.SelectedItem is EditableRow row && row.State != RowEditState.None)
            {
                _viewModel.UndoRow(row);
                RefreshRowVisuals();
            }
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            e.Handled = true;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

            // Parse TSV: rows separated by newlines, columns by tabs
            var lines = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)  // Skip trailing empty line
                .Select(l => l.Split('\t'))
                .ToList();

            if (lines.Count == 0) return;

            // Paste starting at selected row, or append at end
            var startIndex = ResultsGrid.SelectedIndex >= 0
                ? ResultsGrid.SelectedIndex
                : _viewModel.EditableRows?.Count ?? 0;

            _viewModel.PasteRows(lines, startIndex);
            RefreshRowVisuals();
        }
    }

    private void UpdateEditModeButton()
    {
        if (_viewModel == null) return;

        if (_viewModel.IsEditMode)
        {
            EditModeButton.Content = "Editing";
            EditModeButton.Background = GetRowBrush("PlanScanOrange");
        }
        else
        {
            EditModeButton.Content = "Edit";
            EditModeButton.Background = GetRowBrush("ButtonSecondary");
        }
    }

    private void UpdateEditBar()
    {
        if (_viewModel == null) return;

        var editing = _viewModel.IsEditMode;
        PendingChangesText.IsVisible = editing;
        AddRowButton.IsVisible = editing;
        ShowSqlButton.IsVisible = editing;
        ApplyButton.IsVisible = editing;
        CancelButton.IsVisible = editing;
        EditSeparator.IsVisible = editing;

        if (editing)
        {
            var count = _viewModel.PendingChangeCount;
            PendingChangesText.Text = count == 0
                ? "No changes"
                : $"{count} change{(count == 1 ? "" : "s")} pending";
        }
    }

    private void SetupEditContextMenu()
    {
        var menu = new ContextMenu();

        var deleteItem = new MenuItem { Header = "Mark for Delete" };
        deleteItem.Click += (_, _) =>
        {
            if (ResultsGrid.SelectedItem is EditableRow row)
            {
                _viewModel?.MarkRowForDeleteCommand.Execute(row);
                // Refresh the row visual
                RefreshRowVisuals();
            }
        };
        menu.Items.Add(deleteItem);

        var undeleteItem = new MenuItem { Header = "Undelete" };
        undeleteItem.Click += (_, _) =>
        {
            if (ResultsGrid.SelectedItem is EditableRow row && row.State == RowEditState.Deleted)
            {
                _viewModel?.MarkRowForDeleteCommand.Execute(row);
                RefreshRowVisuals();
            }
        };
        menu.Items.Add(undeleteItem);

        ResultsGrid.ContextMenu = menu;
    }

    private void OnDataGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Row numbers in row header
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();

        if (e.Row.DataContext is EditableRow editRow)
        {
            ApplyRowBackground(e.Row, editRow);
        }
        else
        {
            // Alternating row colors for read-only results
            e.Row.Background = e.Row.GetIndex() % 2 == 1 ? AlternateBrush : Brushes.Transparent;
        }

        // Style NULL cells (grey italic) for both read-only and edit mode
        StyleNullCells(e.Row);

        // Apply column-scoped cell selection highlight for virtualized rows
        ApplyCellSelectionToRow(e.Row);
    }

    private void StyleNullCells(DataGridRow row)
    {
        row.LayoutUpdated += OnRowLayoutForNulls;

        void OnRowLayoutForNulls(object? s, EventArgs args)
        {
            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();
            if (cells.Count == 0) return; // Visual tree not ready, keep listening

            row.LayoutUpdated -= OnRowLayoutForNulls;

            object?[]? values = row.DataContext switch
            {
                object?[] arr => arr,
                EditableRow er => er.Values,
                _ => null
            };
            if (values == null) return;

            for (int i = 0; i < values.Length && i < cells.Count; i++)
            {
                if (values[i] == null || values[i] == DBNull.Value)
                    cells[i].Classes.Add("nullCell");
                else
                    cells[i].Classes.Remove("nullCell");
            }
        }
    }

    private void OnDataGridRowEditEnded(object? sender, DataGridRowEditEndedEventArgs e)
    {
        // IEditableObject.EndEdit handles state tracking.
        // We just need to update the UI.
        if (e.Row.DataContext is EditableRow row)
            ApplyRowBackground(e.Row, row);

        _viewModel?.UpdatePendingChangeCount();
    }

    private void ApplyRowBackground(DataGridRow row, EditableRow editRow)
    {
        row.Background = editRow.State switch
        {
            RowEditState.Modified => ModifiedBrush,
            RowEditState.New => NewBrush,
            RowEditState.Deleted => DeletedBrush,
            _ => row.GetIndex() % 2 == 1 ? AlternateBrush : Brushes.Transparent
        };

        row.Opacity = editRow.State == RowEditState.Deleted ? 0.5 : 1.0;
    }

    private async Task PromptExitEditModeAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;

        // Reuse CloseTabDialog — "Don't Save" = discard changes, "Cancel" = stay in edit mode
        var dialog = new CloseTabDialog("uncommitted edit changes");
        await dialog.ShowDialog(window);

        if (dialog.Result == false) // Don't Save = discard changes
        {
            _viewModel?.CancelChangesCommand.Execute(null);
        }
        else if (dialog.Result == true) // Save = apply changes to database
        {
            _viewModel?.ApplyChangesCommand.Execute(null);
        }
        // null = cancelled — stay in edit mode
    }

    private void RefreshRowVisuals()
    {
        // Force DataGrid to re-evaluate row visuals
        // Toggle ItemsSource to trigger LoadingRow
        if (_viewModel?.EditableRows != null)
        {
            var source = _viewModel.EditableRows;
            ResultsGrid.ItemsSource = null;
            ResultsGrid.ItemsSource = source;
        }
    }

    private async void OnShowSqlClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var sql = _viewModel.GeneratePreviewSql();
        if (string.IsNullOrEmpty(sql))
        {
            sql = "-- No pending changes";
        }

        var textBox = new TextBox
        {
            Text = sql,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Menlo, Monaco, monospace"),
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Margin = new Thickness(0)
        };

        var copyButton = new Button
        {
            Content = "Copy SQL",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        var capturedSql = sql;
        Window? dialogRef = null;
        copyButton.Click += async (_, _) =>
        {
            var clipboard = dialogRef != null ? TopLevel.GetTopLevel(dialogRef)?.Clipboard : null;
            clipboard ??= TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(capturedSql);
                copyButton.Content = "\u2713 Copied";
                await Task.Delay(1200);
                copyButton.Content = "Copy SQL";
            }
        };

        var layout = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(copyButton, Avalonia.Controls.Dock.Bottom);
        layout.Children.Add(copyButton);
        layout.Children.Add(new ScrollViewer { Content = textBox });

        var dialog = new Window
        {
            Title = "Preview SQL — Copy to save your changes if disconnected",
            Width = 650,
            Height = 420,
            Content = layout,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialogRef = dialog;

        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent != null)
            await dialog.ShowDialog(parent);
    }
}
