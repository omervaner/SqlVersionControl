using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SqlVersionControl.Converters;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class QueryTabView
{
    private bool _resultsMaximized;

    // Column-scoped cell selection highlighting
    private DataGridColumn? _highlightedColumn;

    // Drag-to-select state
    private bool _isDragSelecting;
    private int _dragStartRowIndex = -1;
    private int _dragStartColIndex = -1;
    private int _dragEndColIndex = -1;

    // When true, RepaintCellSelection highlights all cells (full row) instead of one column
    private bool _fullRowSelectionMode;

    // NULL styling is handled by OnDataGridLoadingRow → StyleNullCells (text + italic + foreground)
    private static IBrush? _nullForeground;

    /// <summary>Fired when user wants to open a source query in a new tab.</summary>
    public event Action<string>? OpenSourceQueryRequested;

    private const int MessagesTabTag = -1000;
    private const int TraceTabTag = -2000;
    private const int PlanTabTag = -3000;
    private const int StackedResultsTag = -4000;
    private bool _hasPlan;

    /// <summary>Tracks the last-interacted DataGrid (for cell detail, export, copy in stacked mode).</summary>
    private DataGrid _activeResultsGrid = null!; // Set in Initialize() to ResultsGrid

    /// <summary>True when showing stacked multi-result view.</summary>
    private bool _isStackedMode;

    private static IBrush GetNullForeground()
    {
        if (_nullForeground == null || _nullForeground is SolidColorBrush)
        {
            if (Application.Current?.Resources.TryGetResource("TextNull", null, out var brush) == true && brush is IBrush b)
                _nullForeground = b;
            else
                _nullForeground = new SolidColorBrush(Color.FromRgb(102, 102, 102));
        }
        return _nullForeground;
    }

    private static IBrush GetCellSelectionBrush()
    {
        if (Application.Current?.Resources.TryGetResource("CellSelectionHighlight", null, out var brush) == true && brush is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse("#3044688B"));
    }

    /// <summary>
    /// Repaint cell-level selection highlights on the active grid.
    /// Clears all cell highlights, then applies highlight only to cells
    /// in the focused column for selected rows.
    /// </summary>
    private void RepaintCellSelection()
    {
        var grid = _activeResultsGrid;
        if (grid == null) return;

        // Determine column range to highlight
        int colRangeMin, colRangeMax;
        if (_fullRowSelectionMode)
        {
            colRangeMin = -1; // -1 signals "all columns"
            colRangeMax = -1;
        }
        else if (_dragStartColIndex >= 0 && _dragEndColIndex >= 0)
        {
            // Multi-column drag range
            colRangeMin = Math.Min(_dragStartColIndex, _dragEndColIndex);
            colRangeMax = Math.Max(_dragStartColIndex, _dragEndColIndex);
        }
        else
        {
            // Single column from CurrentColumn (click or shift+click)
            var focusedCol = grid.CurrentColumn;
            var idx = focusedCol != null ? grid.Columns.IndexOf(focusedCol) : -1;
            colRangeMin = idx;
            colRangeMax = idx;
        }

        // Get the set of selected items for fast lookup
        var selectedItems = grid.SelectedItems;
        var selectedSet = new HashSet<object>();
        if (selectedItems != null)
        {
            foreach (var item in selectedItems)
            {
                if (item != null) selectedSet.Add(item);
            }
        }

        var highlightBrush = GetCellSelectionBrush();

        // Walk all realized rows
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            bool isSelected = row.DataContext != null && selectedSet.Contains(row.DataContext);

            // Hide Avalonia's built-in selection rectangle (template part)
            if (isSelected)
            {
                foreach (var rect in row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>())
                {
                    rect.Fill = Brushes.Transparent;
                }
            }

            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();

            for (int i = 0; i < cells.Count; i++)
            {
                // Highlight cells in the column range, or ALL cells if full-row mode
                bool inRange = colRangeMin == -1 || (i >= colRangeMin && i <= colRangeMax);
                if (isSelected && inRange)
                    cells[i].Background = highlightBrush;
                else
                    cells[i].Background = Brushes.Transparent;
            }
        }
    }

    /// <summary>
    /// Apply cell highlight to a single row (used in LoadingRow for virtualized rows).
    /// </summary>
    private void ApplyCellSelectionToRow(DataGridRow row)
    {
        var grid = _activeResultsGrid;
        if (grid == null) return;

        // Determine column range (same logic as RepaintCellSelection)
        int colRangeMin, colRangeMax;
        if (_fullRowSelectionMode)
        {
            colRangeMin = -1;
            colRangeMax = -1;
        }
        else if (_dragStartColIndex >= 0 && _dragEndColIndex >= 0)
        {
            colRangeMin = Math.Min(_dragStartColIndex, _dragEndColIndex);
            colRangeMax = Math.Max(_dragStartColIndex, _dragEndColIndex);
        }
        else
        {
            var focusedCol = grid.CurrentColumn;
            var idx = focusedCol != null ? grid.Columns.IndexOf(focusedCol) : -1;
            colRangeMin = idx;
            colRangeMax = idx;
        }

        bool isSelected = row.DataContext != null && grid.SelectedItems != null &&
                          grid.SelectedItems.Contains(row.DataContext);

        row.LayoutUpdated += OnRowLayoutForCellSelection;

        void OnRowLayoutForCellSelection(object? s, EventArgs args)
        {
            row.LayoutUpdated -= OnRowLayoutForCellSelection;

            // Hide Avalonia's built-in selection rectangle
            if (isSelected)
            {
                foreach (var rect in row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>())
                    rect.Fill = Brushes.Transparent;
            }

            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();
            var highlightBrush = GetCellSelectionBrush();

            for (int i = 0; i < cells.Count; i++)
            {
                bool inRange = colRangeMin == -1 || (i >= colRangeMin && i <= colRangeMax);
                if (isSelected && inRange)
                    cells[i].Background = highlightBrush;
                else
                    cells[i].Background = Brushes.Transparent;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Drag-to-select: Avalonia DataGrid doesn't support click-drag selection
    // natively. We implement it via PointerPressed/Moved/Released.
    // -----------------------------------------------------------------------

    private static int GetRowIndexAtPoint(DataGrid grid, Point position)
    {
        var hit = grid.GetVisualAt(position);
        var visual = hit as Visual;
        while (visual != null && visual is not DataGridRow)
            visual = visual.GetVisualParent() as Visual;
        return visual is DataGridRow row ? row.GetIndex() : -1;
    }

    private static int GetColIndexAtPoint(DataGrid grid, Point position)
    {
        // Use column actual widths to determine which column the X position falls in.
        // RowHeaderWidth can be NaN if not explicitly set — measure from the first visible row header instead.
        double headerWidth = grid.RowHeaderWidth;
        if (double.IsNaN(headerWidth))
        {
            // Find actual row header width from the visual tree
            var firstRow = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault();
            var rowHeader = firstRow?.GetVisualDescendants()
                .FirstOrDefault(v => v.GetType().Name == "DataGridRowHeader");
            headerWidth = rowHeader?.Bounds.Width ?? 40;
        }
        double x = position.X - headerWidth;

        // Account for horizontal scroll
        var scrollViewer = grid.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer != null)
            x += scrollViewer.Offset.X;

        if (x < 0) return -1;

        double accumulated = 0;
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            accumulated += grid.Columns[i].ActualWidth;
            if (x < accumulated)
                return i;
        }
        return grid.Columns.Count - 1; // past the last column, clamp to last
    }

    private void OnDragSelectPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (_viewModel is { IsEditMode: true }) return;

        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed) return;

        // Don't interfere with Ctrl+Click or Shift+Click
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        // Don't start drag from column headers
        var source = e.Source as Visual;
        while (source != null)
        {
            if (source is DataGridColumnHeader)
                return;
            source = source.GetVisualParent() as Visual;
        }

        // Don't start drag if click is in the row header area (left of first column)
        var pos = e.GetPosition(grid);
        if (pos.X < grid.RowHeaderWidth)
            return;

        var rowIndex = GetRowIndexAtPoint(grid, pos);
        if (rowIndex < 0) return;

        var colIndex = GetColIndexAtPoint(grid, pos);

        _dragStartRowIndex = rowIndex;
        _dragStartColIndex = colIndex;
        _dragEndColIndex = colIndex;
        _isDragSelecting = false;
        e.Pointer.Capture(grid);
    }

    private void OnDragSelectPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartRowIndex < 0) return;
        if (sender is not DataGrid grid) return;

        var pos = e.GetPosition(grid);
        var currentRowIndex = GetRowIndexAtPoint(grid, pos);
        if (currentRowIndex < 0) return;

        var currentColIndex = GetColIndexAtPoint(grid, pos);
        if (currentColIndex < 0) currentColIndex = _dragEndColIndex; // keep last known

        bool rowChanged = currentRowIndex != _dragStartRowIndex;
        bool colChanged = _dragStartColIndex >= 0 && currentColIndex >= 0 && currentColIndex != _dragStartColIndex;

        if (!rowChanged && !colChanged && !_isDragSelecting)
            return;

        _isDragSelecting = true;
        _dragEndColIndex = currentColIndex;

        // Select the row range from drag start to current row
        var items = grid.ItemsSource?.Cast<object>().ToList();
        if (items == null) return;

        var minIdx = Math.Min(_dragStartRowIndex, currentRowIndex);
        var maxIdx = Math.Max(_dragStartRowIndex, currentRowIndex);

        grid.SelectedItems.Clear();
        for (int i = minIdx; i <= maxIdx; i++)
        {
            if (i >= 0 && i < items.Count)
                grid.SelectedItems.Add(items[i]);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(RepaintCellSelection, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnDragSelectPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _isDragSelecting;
        _isDragSelecting = false;
        _dragStartRowIndex = -1;
        if (!wasDragging)
        {
            _dragStartColIndex = -1;
            _dragEndColIndex = -1;
        }

        e.Pointer.Capture(null);

        if (wasDragging)
        {
            e.Handled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(RepaintCellSelection, Avalonia.Threading.DispatcherPriority.Background);
        }
        else
        {
            // Normal click release — repaint for the single selection
            Avalonia.Threading.Dispatcher.UIThread.Post(RepaintCellSelection, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void OnRowHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Check if click came from a row header area (not a data cell)
        var source = e.Source as Visual;
        bool isRowHeader = false;
        while (source != null)
        {
            // Row header cells have DataGridRowHeader type, but it's internal.
            // Check by type name instead.
            var typeName = source.GetType().Name;
            if (typeName == "DataGridRowHeader" || typeName == "DataGridTopLeftCornerHeader")
            {
                isRowHeader = true;
                break;
            }
            if (source is DataGridCell)
                break; // It's a data cell click, not a header
            source = source.GetVisualParent() as Visual;
        }

        if (isRowHeader)
        {
            _fullRowSelectionMode = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(RepaintCellSelection, Avalonia.Threading.DispatcherPriority.Background);
        }
        else
        {
            _fullRowSelectionMode = false;
        }
    }

    /// <summary>Wire drag-to-select and row header detection on a DataGrid (main or stacked).</summary>
    private void WireDragSelection(DataGrid grid)
    {
        grid.AddHandler(PointerPressedEvent, OnRowHeaderPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerPressedEvent, OnDragSelectPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerMovedEvent, OnDragSelectPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        grid.AddHandler(PointerReleasedEvent, OnDragSelectPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildResultTabs();

        // Auto-expand results panel, sized to fit content (capped at 50%)
        try
        {
            if (_viewModel?.Results.Count > 0)
            {
                _resultsCollapsed = false;

                var totalHeight = EditorResultsGrid.Bounds.Height;
                if (totalHeight <= 0) totalHeight = 600;
                var resultCount = _viewModel.Results.Count(r => r.Error == null);
                var maxResultsHeight = resultCount > 1 ? totalHeight * 0.65 : totalHeight * 0.5;

                // Calculate height needed: header bar (28) + rows × row height
                var rowHeight = _settings?.Settings.GridRowHeight ?? 22;
                var goodResults = _viewModel.Results.Where(r => r.Error == null).ToList();
                double neededHeight;
                if (goodResults.Count > 1)
                {
                    // Stacked: sum all results (header bar per result + rows + column header)
                    neededHeight = 28; // tab bar
                    foreach (var r in goodResults)
                        neededHeight += 28 + (Math.Min(r.RowCount, 8) + 1) * rowHeight; // cap per-grid preview at 8 rows
                    neededHeight += 10;
                }
                else
                {
                    var rowCount = goodResults.Count > 0 ? goodResults[0].RowCount : 0;
                    neededHeight = 28 + (rowCount + 2) * rowHeight + 10; // +1 header row, +1 buffer row, +10 chrome
                }

                var resultHeight = Math.Min(neededHeight, maxResultsHeight);
                var minHeight = Math.Max(150, totalHeight * 0.2); // at least 20% or 150px
                resultHeight = Math.Max(resultHeight, minHeight);

                EditorResultsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                EditorResultsGrid.RowDefinitions[2].Height = new GridLength(resultHeight, GridUnitType.Pixel);
                ResultsSplitter.IsEnabled = true;
                ResultsCollapseButton.Content = "\u25BC"; // ▼
                if (_settings != null)
                {
                    _settings.Settings.ResultsPanelCollapsed = false;
                    _settings.Save();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryTabView.AutoExpandResults", ex);
        }
    }

    // ── Results Panel Collapse ──────────────────────────────────────

    public void ToggleResultsPanel()
    {
        try
        {
            var rowDefs = EditorResultsGrid.RowDefinitions;
            if (_resultsCollapsed)
            {
                // Expand — restore saved height
                var h = _settings?.Settings.ResultsPanelHeight ?? 200;
                if (h <= 0 || double.IsNaN(h) || double.IsInfinity(h)) h = 200;
                rowDefs[2].Height = new GridLength(h, GridUnitType.Pixel);
                ResultsSplitter.IsEnabled = true;
                ResultsCollapseButton.Content = "\u25BC"; // ▼
                _resultsCollapsed = false;
            }
            else
            {
                // Save current height before collapsing
                var currentHeight = rowDefs[2].ActualHeight;
                if (currentHeight > 30 && !double.IsNaN(currentHeight) && !double.IsInfinity(currentHeight) && _settings != null)
                {
                    _settings.Settings.ResultsPanelHeight = currentHeight;
                }
                rowDefs[2].Height = new GridLength(0, GridUnitType.Pixel);
                ResultsSplitter.IsEnabled = false;
                ResultsCollapseButton.Content = "\u25B2"; // ▲
                _resultsCollapsed = true;
            }

            if (_settings != null)
            {
                _settings.Settings.ResultsPanelCollapsed = _resultsCollapsed;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryTabView.ToggleResultsPanel", ex);
        }
    }

    private void ToggleResultsMaximized()
    {
        if (_resultsCollapsed) return; // nothing to maximize if collapsed

        var rowDefs = EditorResultsGrid.RowDefinitions;
        var totalHeight = EditorResultsGrid.Bounds.Height;
        if (totalHeight <= 0) return;

        if (_resultsMaximized)
        {
            // Restore to auto-sized based on row count
            _resultsMaximized = false;
            if (_viewModel?.Results.Count > 0)
            {
                var rowHeight = _settings?.Settings.GridRowHeight ?? 22;
                var rowCount = _viewModel.Results[0].RowCount;
                var neededHeight = 28 + (rowCount + 2) * rowHeight + 10;
                var resultHeight = Math.Min(neededHeight, totalHeight * 0.5);
                var minHeight = Math.Max(150, totalHeight * 0.2);
                resultHeight = Math.Max(resultHeight, minHeight);

                rowDefs[0].Height = new GridLength(1, GridUnitType.Star);
                rowDefs[2].Height = new GridLength(resultHeight, GridUnitType.Pixel);
            }
            else
            {
                rowDefs[0].Height = new GridLength(7, GridUnitType.Star);
                rowDefs[2].Height = new GridLength(3, GridUnitType.Star);
            }
        }
        else
        {
            // Maximize results to 50/50
            _resultsMaximized = true;
            rowDefs[0].Height = new GridLength(1, GridUnitType.Star);
            rowDefs[2].Height = new GridLength(1, GridUnitType.Star);
        }
    }

    private void RestoreResultsPanelState()
    {
        try
        {
            if (_settings == null) return;
            var s = _settings.Settings;

            // Validate saved height
            if (s.ResultsPanelHeight <= 0 || double.IsNaN(s.ResultsPanelHeight) || double.IsInfinity(s.ResultsPanelHeight))
                s.ResultsPanelHeight = 200;

            if (s.ResultsPanelCollapsed)
            {
                EditorResultsGrid.RowDefinitions[2].Height = new GridLength(0, GridUnitType.Pixel);
                ResultsSplitter.IsEnabled = false;
                ResultsCollapseButton.Content = "\u25B2"; // ▲
                _resultsCollapsed = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryTabView.RestoreResultsPanelState", ex);
        }
    }

    private void RebuildResultTabs()
    {
        if (_viewModel == null) return;

        ResultTabHeaders.Children.Clear();
        _selectedTabIndex = -1;
        _pinnedTabIndices.Clear();

        var results = _viewModel.Results;
        var hasMessages = _viewModel.Messages.Count > 0;
        var hasTabs = _pinnedResults.Count > 0 || results.Count > 0 || hasMessages || _hasPlan;

        if (!hasTabs)
        {
            ResultsGrid.IsVisible = false;
            MessagesPanel.IsVisible = false;
            ResultsTabBar.IsVisible = false;
            EmptyState.IsVisible = true;
            return;
        }

        ResultsTabBar.IsVisible = true;
        EmptyState.IsVisible = false;

        // Pinned tabs first (tag = -(pinnedIdx + 1))
        for (int p = 0; p < _pinnedResults.Count; p++)
        {
            var (pinnedResult, pinnedLabel) = _pinnedResults[p];
            var pinnedTag = -(p + 1);
            var tabIdx = ResultTabHeaders.Children.Count;
            _pinnedTabIndices.Add(tabIdx);

            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "\uD83D\uDCCC", FontSize = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Opacity = 0.7 }); // 📌
            panel.Children.Add(new TextBlock { Text = pinnedLabel, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

            // × close button
            var capturedPinnedIdx = p;
            var closeBtn = new Button
            {
                Content = "\u00d7",
                FontSize = 10,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(2, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = GetRowBrush("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 0, MinHeight = 0,
            };
            closeBtn.Click += (_, _) => UnpinResultTab(capturedPinnedIdx);
            ToolTip.SetTip(closeBtn, "Unpin");
            panel.Children.Add(closeBtn);

            var pTag = pinnedTag;
            var btn = new Button
            {
                Content = panel,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0),
                FontSize = 11,
                Foreground = GetRowBrush("TextPrimary"),
                Background = GetRowBrush("SidebarBackground"),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(1, 0, 1, 2),
                BorderBrush = GetRowBrush("BorderDefault"),
                Tag = pTag
            };
            btn.Click += (_, _) => SelectResultTab(pTag);
            btn.ContextMenu = BuildResultTabContextMenu(pinnedResult, pTag);
            ResultTabHeaders.Children.Add(btn);
        }

        // Live result tabs
        var goodResults = results.Where(r => r.Error == null).ToList();
        var hasErrors = results.Any(r => r.Error != null);

        if (goodResults.Count > 1)
        {
            // Multi-result: single combined "Results" tab → stacked view
            var totalRows = goodResults.Sum(r => r.RowCount);
            var label = $"Results ({goodResults.Count} sets, {totalRows:N0} rows)";

            var btn = new Button
            {
                Content = label,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0),
                FontSize = 11,
                Foreground = GetRowBrush("TextSecondary"),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Tag = StackedResultsTag
            };
            btn.Click += (_, _) => SelectResultTab(StackedResultsTag);
            ResultTabHeaders.Children.Add(btn);
        }
        else if (goodResults.Count == 1)
        {
            // Single result: same as before — one tab with pin button
            var singleIdx = results.IndexOf(goodResults[0]);
            var r = goodResults[0];
            var label = $"Result ({r.RowCount:N0} rows)";

            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

            // Pin button
            var pinBtn = new Button
            {
                Content = "\u25CF",
                FontSize = 9,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = GetRowBrush("TextSecondary"),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Opacity = 0.5
            };
            ToolTip.SetTip(pinBtn, "Pin this result");
            var capturedLabel = label;
            var capturedIdx = singleIdx;
            pinBtn.Click += (_, _) => PinResultTab(capturedIdx, capturedLabel);
            panel.Children.Add(pinBtn);

            // Copy button
            panel.Children.Add(BuildCopyButton(r));

            var btn = new Button
            {
                Content = panel,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0),
                FontSize = 11,
                Foreground = GetRowBrush("TextSecondary"),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Tag = singleIdx
            };
            btn.Click += (_, _) => SelectResultTab(singleIdx);
            btn.ContextMenu = BuildResultTabContextMenu(r, singleIdx);
            ResultTabHeaders.Children.Add(btn);
        }

        // Messages tab (with error count badge if errors exist)
        var errorCount = _viewModel.Messages.Count(m => m.Type == SqlVersionControl.Models.MessageType.Error);
        object msgContent;
        if (errorCount > 0)
        {
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text = "Messages",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            panel.Children.Add(new Border
            {
                Background = GetRowBrush("WarningSeverityCritical"),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 0),
                MinWidth = 14,
                Child = new TextBlock
                {
                    Text = errorCount.ToString(),
                    FontSize = 9,
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            });
            msgContent = panel;
        }
        else
        {
            msgContent = "Messages";
        }

        var msgBtn = new Button
        {
            Content = msgContent,
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0),
            FontSize = 11,
            Foreground = GetRowBrush("TextSecondary"),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent,
            Tag = -1000 // Special messages marker
        };
        msgBtn.Click += (_, _) => SelectMessagesTab();
        ResultTabHeaders.Children.Add(msgBtn);

        // Trace tab (only visible when trace events exist)
        if (_viewModel.TraceEvents.Count > 0)
        {
            var traceBtn = new Button
            {
                Content = $"Trace ({_viewModel.TraceEvents.Count})",
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0),
                FontSize = 11,
                Foreground = GetRowBrush("TextSecondary"),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Tag = -2000 // Special trace marker
            };
            traceBtn.Click += (_, _) => SelectTraceTab();
            ResultTabHeaders.Children.Add(traceBtn);
        }

        // Exec Plan tab (only visible when plan has been generated)
        if (_hasPlan)
        {
            var planBtn = new Button
            {
                Content = "Exec Plan",
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0),
                FontSize = 11,
                Foreground = GetRowBrush("TextSecondary"),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Tag = PlanTabTag
            };
            planBtn.Click += (_, _) => SelectPlanTab();
            ResultTabHeaders.Children.Add(planBtn);
        }

        // Auto-select first live result
        if (goodResults.Count > 1)
        {
            SelectResultTab(StackedResultsTag);
        }
        else if (goodResults.Count == 1)
        {
            SelectResultTab(results.IndexOf(goodResults[0]));
        }
        else if (results.Count > 0)
        {
            // All results are errors
            SelectMessagesTab();
        }
        else if (_pinnedResults.Count > 0)
        {
            SelectResultTab(-1); // First pinned tab
        }
        else
        {
            SelectMessagesTab();
        }
    }

    private void PinResultTab(int liveIndex, string label)
    {
        if (_viewModel == null || liveIndex < 0 || liveIndex >= _viewModel.Results.Count) return;
        var result = _viewModel.Results[liveIndex];
        var timestamp = DateTime.Now.ToString("HH:mm");
        _pinnedResults.Add((result, $"{label} - {timestamp}"));
        RebuildResultTabs();
    }

    private void UnpinResultTab(int pinnedIndex)
    {
        if (pinnedIndex < 0 || pinnedIndex >= _pinnedResults.Count) return;
        _pinnedResults.RemoveAt(pinnedIndex);
        RebuildResultTabs();
    }

    private ContextMenu BuildResultTabContextMenu(QueryResult result, int tag)
    {
        var menu = new ContextMenu();

        var openSource = new MenuItem { Header = "Open Source Query" };
        openSource.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(result.SourceSql))
                OpenSourceQueryRequested?.Invoke(result.SourceSql);
        };
        menu.Items.Add(openSource);

        // Pinned tabs get an Unpin option, live tabs get Pin
        if (tag < 0 && tag != MessagesTabTag)
        {
            var pinnedIdx = -(tag + 1);
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) => UnpinResultTab(pinnedIdx);
            menu.Items.Add(unpin);
        }

        menu.Opening += (_, _) =>
        {
            openSource.IsEnabled = !string.IsNullOrEmpty(result.SourceSql);
        };

        return menu;
    }

    /// <summary>
    /// Select a result tab. Positive index = live result, negative = pinned (-(pinnedIdx+1)),
    /// or StackedResultsTag for the combined stacked view.
    /// </summary>
    private void SelectResultTab(int index)
    {
        if (_viewModel == null) return;

        // Stacked results view (multi-result combined tab)
        if (index == StackedResultsTag)
        {
            if (_viewModel.IsEditMode)
                _viewModel.CancelChangesCommand.Execute(null);

            _selectedTabIndex = index;
            _isStackedMode = true;
            MessagesPanel.IsVisible = false;
            TracePanel.IsVisible = false;
            PlanPanel.IsVisible = false;
            EmptyState.IsVisible = false;
            CellDetailPanel.IsVisible = false;
            ResultsGrid.IsVisible = false;

            ShowStackedResults();
            StackedResultsPanel.IsVisible = true;
            UpdateTabHighlight(index);

            // Disable edit mode in stacked view
            EditModeButton.IsEnabled = false;
            EditModeButton.Opacity = 0.4;
            ToolTip.SetShowOnDisabled(EditModeButton, true);
            ToolTip.SetTip(EditModeButton, "Edit mode is not available for multi-result queries");
            return;
        }

        QueryResult? result;

        if (index < 0)
        {
            // Pinned tab: -(pinnedIdx + 1)
            var pinnedIdx = -(index + 1);
            if (pinnedIdx < 0 || pinnedIdx >= _pinnedResults.Count) return;
            result = _pinnedResults[pinnedIdx].Result;
        }
        else
        {
            if (index >= _viewModel.Results.Count) return;
            result = _viewModel.Results[index];
        }

        // Exit edit mode if switching result tabs
        if (_viewModel.IsEditMode)
        {
            _viewModel.CancelChangesCommand.Execute(null);
        }

        _selectedTabIndex = index;
        _isStackedMode = false;
        MessagesPanel.IsVisible = false;
        TracePanel.IsVisible = false;
        PlanPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        StackedResultsPanel.IsVisible = false;

        // Restore edit button state (may have been disabled for stacked mode)
        EditModeButton.IsEnabled = true;
        EditModeButton.Opacity = 1.0;
        ToolTip.SetTip(EditModeButton, "Edit result rows");

        if (result.Error != null)
        {
            SelectMessagesTab();
            return;
        }

        BuildColumns(result);
        ResultsGrid.ItemsSource = result.Rows;
        ResultsGrid.IsReadOnly = true;
        ResultsGrid.IsVisible = true;
        _activeResultsGrid = ResultsGrid;
        SetupReadOnlyContextMenu();
        UpdateTabHighlight(index);
    }

    /// <summary>Build the stacked results view with one DataGrid per result set, with splitters.</summary>
    private void ShowStackedResults()
    {
        if (_viewModel == null) return;

        StackedResultsPanel.Children.Clear();
        StackedResultsPanel.RowDefinitions.Clear();
        var results = _viewModel.Results.Where(r => r.Error == null).ToList();
        if (results.Count == 0) return;

        var rowHeight = _settings?.Settings.GridRowHeight ?? 22;
        var gridRow = 0;

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var resultNumber = i + 1;

            // Splitter between results (not before first)
            if (i > 0)
            {
                StackedResultsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var splitter = new GridSplitter
                {
                    Height = 4,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth),
                };
                if (Application.Current?.Resources.TryGetResource("BorderDefault", null, out var splitterBg) == true && splitterBg is IBrush splitterBrush)
                    splitter.Background = splitterBrush;
                Grid.SetRow(splitter, gridRow);
                StackedResultsPanel.Children.Add(splitter);
                gridRow++;
            }

            // Container for header + grid — empty results get Auto (just header + column headers),
            // others get Star weight proportional to row count (capped at 20)
            if (result.RowCount == 0)
            {
                StackedResultsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            else
            {
                var weight = Math.Max(1, Math.Min(result.RowCount, 20));
                StackedResultsPanel.RowDefinitions.Add(new RowDefinition(new GridLength(weight, GridUnitType.Star)));
            }

            var container = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(new GridLength(1, GridUnitType.Star)) },
            };
            Grid.SetRow(container, gridRow);
            gridRow++;

            // Header bar
            var header = new Border { Padding = new Thickness(10, 4) };
            if (Application.Current?.Resources.TryGetResource("PanelHeaderBackground", null, out var bg) == true && bg is IBrush bgBrush)
                header.Background = bgBrush;

            var headerGrid = new Grid { ColumnDefinitions = Avalonia.Controls.ColumnDefinitions.Parse("*,Auto,Auto") };

            var label = new TextBlock
            {
                Text = $"Result {resultNumber} — {result.RowCount:N0} rows",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (Application.Current?.Resources.TryGetResource("TextPrimary", null, out var fg) == true && fg is IBrush fgBrush)
                label.Foreground = fgBrush;
            Grid.SetColumn(label, 0);
            headerGrid.Children.Add(label);

            // Pin button
            var capturedIdx = i;
            var capturedLabel = $"Result {resultNumber} ({result.RowCount} rows)";
            var pinBtn = new Button
            {
                Content = "\u25CF",
                FontSize = 9,
                Padding = new Thickness(4, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Opacity = 0.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (Application.Current?.Resources.TryGetResource("TextSecondary", null, out var pinFg) == true && pinFg is IBrush pinBrush)
                pinBtn.Foreground = pinBrush;
            ToolTip.SetTip(pinBtn, "Pin this result");
            pinBtn.Click += (_, _) => PinResultTab(capturedIdx, capturedLabel);
            Grid.SetColumn(pinBtn, 1);
            headerGrid.Children.Add(pinBtn);

            // Copy button
            var copyBtn = BuildCopyButton(result);
            copyBtn.Padding = new Thickness(4, 2);
            Grid.SetColumn(copyBtn, 2);
            headerGrid.Children.Add(copyBtn);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            container.Children.Add(header);

            // DataGrid for this result
            var gridFontSize = Application.Current?.Resources.TryGetResource("GridFontSize", null, out var fs) == true && fs is double d ? d : 12;
            var grid = new DataGrid
            {
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.All,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                BorderThickness = new Thickness(0),
                CanUserResizeColumns = true,
                CanUserSortColumns = true,
                CanUserReorderColumns = true,
                FrozenColumnCount = 0,
                RowHeight = rowHeight,
                ColumnHeaderHeight = rowHeight + 4,
                FontSize = gridFontSize,
                FontFamily = new FontFamily("Consolas, Menlo, Monaco, monospace"),
            };

            BuildColumnsForGrid(grid, result);
            grid.ItemsSource = result.Rows;

            // Wire events for cell detail + active grid tracking + cell selection highlight
            grid.SelectionChanged += (s, _) =>
            {
                var clickedGrid = (DataGrid)s!;
                _activeResultsGrid = clickedGrid;
                // Clear selection on all other stacked grids
                foreach (var otherGrid in StackedResultsPanel.GetVisualDescendants().OfType<DataGrid>())
                {
                    if (otherGrid != clickedGrid)
                        otherGrid.SelectedIndex = -1;
                }
                UpdateCellDetail();
                Avalonia.Threading.Dispatcher.UIThread.Post(RepaintCellSelection, Avalonia.Threading.DispatcherPriority.Background);
            };
            grid.CellPointerPressed += (s, _) =>
            {
                _activeResultsGrid = (DataGrid)s!;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { UpdateCellDetail(); RepaintCellSelection(); });
            };

            // Wire LoadingRow for NULL styling + keyboard handler for copy + drag-select
            grid.LoadingRow += OnDataGridLoadingRow;
            grid.AddHandler(KeyDownEvent, OnResultsGridKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            WireDragSelection(grid);

            Grid.SetRow(grid, 1);
            container.Children.Add(grid);

            StackedResultsPanel.Children.Add(container);
        }

        // Set active to first grid
        var firstGrid = StackedResultsPanel.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
        if (firstGrid != null)
            _activeResultsGrid = firstGrid;
    }

    /// <summary>
    /// Single source of truth for building result grid columns.
    /// Read-only mode: TwoWay + NullDisplayConverter (shows "NULL" for nulls).
    /// Edit mode: TwoWay, no converter (raw values, empty = null).
    /// </summary>
    private void BuildColumns(QueryResult result) => BuildColumnsForGrid(ResultsGrid, result);

    /// <summary>Build columns on any DataGrid — the actual single source of truth.</summary>
    private static void BuildColumnsForGrid(DataGrid grid, QueryResult result)
    {
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.FrozenColumnCount = 0;

        var fontSize = Application.Current?.Resources.TryGetResource("GridFontSize", null, out var fs) == true && fs is double d ? d : 12.0;

        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnNames[i],
                Binding = new Binding($"[{i}]", BindingMode.TwoWay),
                IsReadOnly = true,
                FontSize = fontSize,
            });
        }
    }

    private void SetColumnsReadOnly(bool readOnly)
    {
        foreach (var col in ResultsGrid.Columns)
            if (col is DataGridBoundColumn bc)
                bc.IsReadOnly = readOnly;
    }

    private void OnColumnHeaderDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // If double-click originated from a column header, consume it so
        // the DataGrid doesn't enter cell edit mode
        var source = e.Source as Avalonia.Visual;
        while (source != null && source is not Avalonia.Controls.DataGridColumnHeader && source != ResultsGrid)
            source = source.GetVisualParent() as Avalonia.Visual;

        if (source is Avalonia.Controls.DataGridColumnHeader)
            e.Handled = true;
    }

    private void OnColumnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;

        // Walk up the visual tree to find the DataGridColumnHeader
        var source = e.Source as Avalonia.Visual;
        while (source != null && source is not Avalonia.Controls.DataGridColumnHeader)
            source = source.GetVisualParent() as Avalonia.Visual;

        if (source is not Avalonia.Controls.DataGridColumnHeader header) return;

        var headerText = header.Content?.ToString() ?? "";
        var colIndex = -1;
        for (int i = 0; i < ResultsGrid.Columns.Count; i++)
        {
            if (ResultsGrid.Columns[i].Header?.ToString() == headerText)
            { colIndex = i; break; }
        }
        if (colIndex < 0) return;

        var colName = headerText;
        var isFrozen = colIndex < ResultsGrid.FrozenColumnCount;

        var menu = new ContextMenu();

        var freezeItem = new MenuItem
        {
            Header = isFrozen ? $"Unfreeze \"{colName}\"" : $"Freeze \"{colName}\""
        };
        freezeItem.Click += (_, _) =>
        {
            if (isFrozen)
                ResultsGrid.FrozenColumnCount = colIndex; // Unfreeze this and all after
            else
                ResultsGrid.FrozenColumnCount = colIndex + 1; // Freeze up to and including this
        };
        menu.Items.Add(freezeItem);

        if (ResultsGrid.FrozenColumnCount > 0)
        {
            var unfreezeAll = new MenuItem { Header = "Unfreeze All" };
            unfreezeAll.Click += (_, _) => ResultsGrid.FrozenColumnCount = 0;
            menu.Items.Add(unfreezeAll);
        }

        menu.Open(header);
        e.Handled = true;
    }

    private Button BuildCopyButton(QueryResult result, DataGrid? targetGrid = null)
    {
        var btn = new Button
        {
            Content = "\uD83D\uDCCB", // 📋
            FontSize = 9,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = GetRowBrush("TextSecondary"),
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Opacity = 0.5
        };
        ToolTip.SetTip(btn, "Copy result set");
        btn.Click += async (_, _) =>
        {
            var grid = targetGrid ?? _activeResultsGrid;
            var selected = new List<object?[]>();
            foreach (var item in grid.SelectedItems)
                if (item is object?[] row) selected.Add(row);

            var rows = selected.Count > 0 ? selected : result.Rows;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join("\t", result.ColumnNames));
            foreach (var row in rows)
                sb.AppendLine(string.Join("\t", row.Select(v => v?.ToString() ?? "NULL")));

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(sb.ToString());
                var label = selected.Count > 0 ? $"{selected.Count} selected row" : $"{rows.Count} row";
                if (rows.Count != 1) label += "s";
                _viewModel?.Flash($"\u2713 {label} copied", ViewModels.QueryStatusSeverity.Success);
            }
        };
        return btn;
    }

    private void SelectMessagesTab()
    {
        _selectedTabIndex = MessagesTabTag;
        _isStackedMode = false;
        ResultsGrid.IsVisible = false;
        StackedResultsPanel.IsVisible = false;
        MessagesPanel.IsVisible = true;
        TracePanel.IsVisible = false;
        PlanPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        UpdateTabHighlight(MessagesTabTag);
    }

    private void SelectTraceTab()
    {
        _selectedTabIndex = TraceTabTag;
        _isStackedMode = false;
        ResultsGrid.IsVisible = false;
        StackedResultsPanel.IsVisible = false;
        MessagesPanel.IsVisible = false;
        TracePanel.IsVisible = true;
        PlanPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        UpdateTabHighlight(TraceTabTag);
    }

    private void SelectPlanTab()
    {
        _selectedTabIndex = PlanTabTag;
        _isStackedMode = false;
        ResultsGrid.IsVisible = false;
        StackedResultsPanel.IsVisible = false;
        MessagesPanel.IsVisible = false;
        TracePanel.IsVisible = false;
        PeekPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        PlanPanel.IsVisible = true;
        UpdateTabHighlight(PlanTabTag);
    }

    /// <summary>
    /// Shows an execution plan in the embedded PlanView panel. Called from QueryEditorHost.
    /// </summary>
    public void ShowExecPlan(string sql, string planXml, string label, DatabaseService db)
    {
        // Initialize embedded PlanView on first use
        if (PlanPanel.ViewModel == null)
            PlanPanel.InitializeEmbedded(db);

        PlanPanel.LoadPlan(sql, planXml, label);
        _hasPlan = true;

        // Expand results panel if collapsed
        if (_resultsCollapsed)
        {
            _resultsCollapsed = false;
            var totalHeight = EditorResultsGrid.Bounds.Height;
            var planHeight = totalHeight > 0 ? totalHeight * 0.5 : 300;
            EditorResultsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(planHeight, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC";
        }

        RebuildResultTabs();
        SelectPlanTab();
    }

    private void UpdateTabHighlight(int selectedIndex)
    {
        var accentBrush = GetRowBrush("ButtonToggleActive");
        var activeFg = GetRowBrush("TextBright");
        var normalFg = GetRowBrush("TextSecondary");

        for (int i = 0; i < ResultTabHeaders.Children.Count; i++)
        {
            if (ResultTabHeaders.Children[i] is Button btn)
            {
                var tag = (int)(btn.Tag ?? MessagesTabTag);
                var isSelected = tag == selectedIndex;

                btn.BorderBrush = isSelected ? accentBrush : Brushes.Transparent;
                btn.Foreground = isSelected ? activeFg : normalFg;
                btn.Background = Brushes.Transparent;
            }
        }
    }
}
