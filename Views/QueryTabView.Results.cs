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

    private static readonly NullDisplayConverter _nullTextConverter = new();
    private static IBrush? _nullForeground;

    /// <summary>Fired when user wants to open a source query in a new tab.</summary>
    public event Action<string>? OpenSourceQueryRequested;

    private const int MessagesTabTag = -1000;
    private const int TraceTabTag = -2000;

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
                var maxResultsHeight = totalHeight * 0.5;

                // Calculate height needed: header bar (28) + rows × row height
                var rowHeight = _settings?.Settings.GridRowHeight ?? 22;
                var firstResult = _viewModel.Results[0];
                var rowCount = firstResult.RowCount;
                var neededHeight = 28 + (rowCount + 2) * rowHeight + 10; // +1 header row, +1 buffer row, +10 chrome

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
        var hasTabs = _pinnedResults.Count > 0 || results.Count > 0 || hasMessages;

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
            panel.Children.Add(new TextBlock { Text = "\u25CF", FontSize = 9, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }); // 📌
            panel.Children.Add(new TextBlock { Text = pinnedLabel, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

            var pTag = pinnedTag;
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
                Tag = pTag
            };
            btn.Click += (_, _) => SelectResultTab(pTag);
            btn.ContextMenu = BuildResultTabContextMenu(pinnedResult, pTag);
            ResultTabHeaders.Children.Add(btn);
        }

        // Live result tabs (tag = positive index)
        var resultNumber = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            string label;
            if (r.Error != null)
            {
                label = "Error";
            }
            else
            {
                resultNumber++;
                label = results.Count(x => x.Error == null) == 1
                    ? $"Result ({r.RowCount} rows)"
                    : $"Result {resultNumber} ({r.RowCount} rows)";
            }

            var idx = i;

            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

            // Pin button
            var pinBtn = new Button
            {
                Content = "\u25CF", // 📌
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
            var capturedIdx = idx;
            var capturedLabel = label;
            pinBtn.Click += (_, _) => PinResultTab(capturedIdx, capturedLabel);
            panel.Children.Add(pinBtn);

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
                Tag = idx
            };
            btn.Click += (_, _) => SelectResultTab(idx);
            btn.ContextMenu = BuildResultTabContextMenu(r, idx);
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

        // Auto-select first live result
        if (results.Count > 0)
        {
            var firstGood = results.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Error == null);
            if (firstGood.r != null)
                SelectResultTab(firstGood.i);
            else
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
    /// Select a result tab. Positive index = live result, negative = pinned (-(pinnedIdx+1)).
    /// </summary>
    private void SelectResultTab(int index)
    {
        if (_viewModel == null) return;

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
        MessagesPanel.IsVisible = false;
        TracePanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;

        if (result.Error != null)
        {
            SelectMessagesTab();
            return;
        }

        BuildColumns(result);
        ResultsGrid.ItemsSource = result.Rows;
        ResultsGrid.IsReadOnly = true;
        ResultsGrid.IsVisible = true;
        SetupReadOnlyContextMenu();
        UpdateTabHighlight(index);
    }

    /// <summary>
    /// Single source of truth for building result grid columns.
    /// Read-only mode: TwoWay + NullDisplayConverter (shows "NULL" for nulls).
    /// Edit mode: TwoWay, no converter (raw values, empty = null).
    /// </summary>
    private void BuildColumns(QueryResult result)
    {
        ResultsGrid.Columns.Clear();
        ResultsGrid.AutoGenerateColumns = false;
        ResultsGrid.FrozenColumnCount = 0;

        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnNames[i],
                Binding = new Binding($"[{i}]", BindingMode.TwoWay),
                IsReadOnly = true,
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

    private void SelectMessagesTab()
    {
        _selectedTabIndex = MessagesTabTag;
        ResultsGrid.IsVisible = false;
        MessagesPanel.IsVisible = true;
        TracePanel.IsVisible = false;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        UpdateTabHighlight(MessagesTabTag);
    }

    private void SelectTraceTab()
    {
        _selectedTabIndex = TraceTabTag;
        ResultsGrid.IsVisible = false;
        MessagesPanel.IsVisible = false;
        TracePanel.IsVisible = true;
        EmptyState.IsVisible = false;
        CellDetailPanel.IsVisible = false;
        UpdateTabHighlight(TraceTabTag);
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
