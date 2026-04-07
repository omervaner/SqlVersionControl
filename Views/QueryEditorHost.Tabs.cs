using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost
{
    // ── Closed Tab Stack (for reopen) ───────────────────────────────

    private record ClosedTabState(
        string SqlText, string? TabTitle, string? SelectedDatabase,
        string? ConnectionString, SavedConnection? ConnectionProfile,
        string? QueryPath, string? QueryName, int CursorPosition);

    private readonly Stack<ClosedTabState> _closedTabs = new();
    private const int MaxClosedTabs = 10;

    // ── Tab Lifecycle ────────────────────────────────────────────────

    public void AddNewTab(string? connectionString = null, SavedConnection? profile = null)
    {
        if (_db == null) return;

        // Inherit from active tab when no explicit connection is provided
        var currentVm = ActiveTabViewModel;
        var connStr = connectionString ?? currentVm?.TabConnectionString ?? _primaryConnectionString;
        var connProfile = profile ?? currentVm?.TabConnectionProfile ?? _primaryProfile;

        _tabCounter++;
        var vm = new QueryTabViewModel(_db)
        {
            TabTitle = $"Query {_tabCounter}",
            TabConnectionString = connStr,
            TabConnectionProfile = connProfile
        };

        // Wire reconnect callback for disconnected connections
        if (_registry != null)
        {
            var registry = _registry;
            vm.ReconnectCallback = async connectionId =>
            {
                var managed = registry.GetById(connectionId);
                if (managed == null) return null;
                if (managed.IsConnected) return managed.ResolvedConnectionString;

                // Ask the user before reconnecting
                var parent = TopLevel.GetTopLevel(this) as Window;
                if (parent == null) return null;

                var dialog = new ConfirmDialog(
                    $"{managed.DisplayName} connection is lost. Do you want to reconnect?",
                    "Yes", "No");
                await dialog.ShowDialog(parent);
                if (!dialog.Confirmed) return null;

                var (success, error) = await registry.ConnectAsync(connectionId);
                if (success) return managed.ResolvedConnectionString;

                return null;
            };
        }

        // Wire confirm-discard-edits callback
        vm.ConfirmDiscardEditsCallback = async message =>
        {
            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent == null) return true;

            var dialog = new ConfirmDialog(message, "Discard", "Cancel");
            await dialog.ShowDialog(parent);
            return dialog.Confirmed;
        };

        // Load databases from appropriate server
        var effectiveConn = vm.TabConnectionString;
        if (effectiveConn != null && effectiveConn != _primaryConnectionString)
        {
            // Different server — check cache or fetch
            _ = LoadDatabasesForTabAsync(vm, effectiveConn);
        }
        else if (_cachedDatabases.Count > 0)
        {
            var selectedDb = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                ? _tabs[_activeTabIndex].DataContext is QueryTabViewModel activeVm
                    ? activeVm.SelectedDatabase
                    : null
                : null;
            vm.SetDatabases(_cachedDatabases, selectedDb);
        }
        else if (effectiveConn != null)
        {
            // Registry mode — _cachedDatabases not populated, fetch directly
            _ = LoadDatabasesForTabAsync(vm, effectiveConn);
        }

        // Refresh tab strip when unsaved changes indicator changes
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueryTabViewModel.HasUnsavedChanges))
                RebuildTabStrip();
            if (e.PropertyName == nameof(QueryTabViewModel.TabTitle))
                UpdateTabButtonText(vm);
            if (e.PropertyName == nameof(QueryTabViewModel.SelectedDatabase))
                OnTabDatabaseChanged(vm);
        };

        // Record query history on successful execution
        vm.QueryExecuted += (sql, db, rows) =>
        {
            _sessionService?.AddQueryToHistory(sql, db, rows);

            // Invalidate intellisense cache if DDL was executed
            if (vm.TabConnectionString != null && db != null && IsDdlStatement(sql))
                _intellisenseCache.Remove($"{vm.TabConnectionString}|{db}");
        };

        var tabView = new QueryTabView();
        tabView.Initialize(vm, _settings);
        tabView.SetAutocompleteCheck(() => IsAutocompleteEnabled);
        tabView.ProcDropRequested += OnProcDropRequested;
        tabView.PeekDefinitionRequested += OnPeekDefinitionRequested;
        tabView.QuickExecuteRequested += OnQuickExecuteRequested;
        tabView.OpenSourceQueryRequested += sql => OpenScriptInNewTab(sql);
        tabView.FilterByValueRequested += sql => OpenScriptInNewTab(sql);
        tabView.FileDropped += path => OpenDroppedFile(path);
        tabView.FormatSqlRequested += FormatSqlInEditor;
        tabView.QuickQuoteRequested += () => QuickQuoteSelection(nPrefix: false);
        tabView.ShowDependenciesRequested += OnShowDependenciesFromEditor;
        tabView.ExecPlanRequested += () => _ = GenerateExecPlanForActiveTab();

        _tabs.Add(tabView);
        TabContentPanel.Children.Add(tabView);

        SwitchToTab(_tabs.Count - 1);

        // Save session on tab created (unless we're restoring)
        if (!_restoringSession)
            SaveSession();
    }

    public async Task CloseTabAsync(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var tabView = _tabs[index];
        var vm = tabView.DataContext as QueryTabViewModel;

        // Prompt if unsaved changes
        if (vm?.HasUnsavedChanges == true)
        {
            var dialog = new CloseTabDialog(vm.TabTitle);
            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent != null)
                await dialog.ShowDialog(parent);

            if (dialog.Result == null) return; // Cancel — abort close
            // dialog.Result == true → Save first, then close
            if (dialog.Result == true)
            {
                var svc = new QueryFileService();
                var settings = GetSettingsService();
                if (settings != null)
                {
                    var saved = await SaveActiveQueryAsync(svc, settings);
                    if (!saved) return; // Save was cancelled — abort close
                }
            }
            // dialog.Result == false → Don't Save — proceed
        }

        // Save state for reopen before removing
        if (vm != null)
        {
            var editor = tabView.FindControl<AvaloniaEdit.TextEditor>("SqlEditor");
            _closedTabs.Push(new ClosedTabState(
                vm.SqlText ?? "", vm.TabTitle, vm.SelectedDatabase,
                vm.TabConnectionString, vm.TabConnectionProfile,
                vm.CurrentQueryPath, vm.CurrentQueryName,
                editor?.CaretOffset ?? 0));
            while (_closedTabs.Count > MaxClosedTabs) _closedTabs.TryPop(out _);
        }

        _tabs.RemoveAt(index);
        TabContentPanel.Children.Remove(tabView);

        // If we closed the last tab, create a fresh one
        if (_tabs.Count == 0)
        {
            AddNewTab();
            return;
        }

        // Adjust active index
        if (_activeTabIndex >= _tabs.Count)
            _activeTabIndex = _tabs.Count - 1;
        else if (_activeTabIndex > index)
            _activeTabIndex--;

        SwitchToTab(_activeTabIndex);

        // Save session on tab closed
        SaveSession();
    }

    public async Task CloseActiveTabAsync()
    {
        if (_activeTabIndex >= 0)
            await CloseTabAsync(_activeTabIndex);
    }

    public void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;

        var state = _closedTabs.Pop();
        AddNewTab(state.ConnectionString, state.ConnectionProfile);

        var tabView = _tabs[^1];
        var vm = tabView.DataContext as QueryTabViewModel;
        if (vm == null) return;

        // Restore state
        var editor = tabView.FindControl<AvaloniaEdit.TextEditor>("SqlEditor");
        if (editor != null) editor.Text = state.SqlText;
        vm.SetInitialText(state.SqlText);
        vm.CurrentQueryPath = state.QueryPath;
        vm.CurrentQueryName = state.QueryName;
        if (state.SelectedDatabase != null) vm.SelectedDatabase = state.SelectedDatabase;
        if (state.QueryName != null) vm.TabTitle = state.QueryName;

        // Mark dirty if no saved file
        if (state.QueryPath == null && !string.IsNullOrWhiteSpace(state.SqlText))
            vm.HasUnsavedChanges = true;

        // Restore cursor
        if (editor != null && state.CursorPosition >= 0 && state.CursorPosition <= state.SqlText.Length)
            editor.CaretOffset = state.CursorPosition;

        SwitchToTab(_tabs.Count - 1);
        RebuildTabStrip();
        SaveSession();
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var changed = _activeTabIndex != index;

        var isMultiConnection = _registry != null && _registry.ActiveConnections.Any();

        // Cache outgoing tab's OE tree before switching (legacy single-connection mode only)
        if (!isMultiConnection && changed && _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count && _viewModel != null)
        {
            var outgoingVm = _tabs[_activeTabIndex].DataContext as QueryTabViewModel;
            var outgoingConn = outgoingVm?.TabConnectionString;
            if (outgoingConn != null)
            {
                if (!_serverCache.ContainsKey(outgoingConn))
                    _serverCache[outgoingConn] = new CachedServerData();
                _serverCache[outgoingConn].OeRootNodes = _viewModel.ObjectExplorer.RootNodes.ToList();
            }
        }

        _activeTabIndex = index;

        // Show/hide tab views
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsVisible = i == index;

        // Update Object Explorer for the new active tab
        if (changed && !_restoringSession)
        {
            var activeVm = _tabs[index].DataContext as QueryTabViewModel;
            if (activeVm != null)
            {
                if (isMultiConnection)
                {
                    // Multi-connection: OE tree is global, just update active connection for highlighting
                    _viewModel?.ObjectExplorer.SetActiveConnection(activeVm.TabConnectionString);
                }
                else
                {
                    // Legacy: rebuild OE tree per-tab connection
                    UpdateObjectExplorerForTab(activeVm);
                }

                // Push cached intellisense service to the tab
                if (activeVm.SelectedDatabase != null)
                    OnTabDatabaseChanged(activeVm);
            }
        }

        RebuildTabStrip();
        ScrollActiveTabIntoView();
        SyncToolbarWithActiveTab();
        ActiveTabChanged?.Invoke();

        // Save session on tab switch
        if (changed && !_restoringSession)
            SaveSession();
    }

    private void ScrollActiveTabIntoView()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= TabStrip.Children.Count) return;

        var tab = TabStrip.Children[_activeTabIndex];
        var sv = TabScrollViewer;
        // Post so layout has completed before we measure
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var tabBounds = tab.Bounds;
            if (tabBounds.Right > sv.Offset.X + sv.Viewport.Width)
                sv.Offset = sv.Offset.WithX(tabBounds.Right - sv.Viewport.Width);
            else if (tabBounds.Left < sv.Offset.X)
                sv.Offset = sv.Offset.WithX(tabBounds.Left);
            UpdateTabOverflowArrows();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    public void SwitchToNextTab()
    {
        if (_tabs.Count <= 1) return;
        SwitchToTab((_activeTabIndex + 1) % _tabs.Count);
    }

    public void SwitchToPreviousTab()
    {
        if (_tabs.Count <= 1) return;
        SwitchToTab((_activeTabIndex - 1 + _tabs.Count) % _tabs.Count);
    }

    /// <summary>Sync toolbar Database combo, Run/Stop enabled state with active tab.</summary>
    private void SyncToolbarWithActiveTab()
    {
        var vm = ActiveTabViewModel;
        if (vm == null) return;

        // Sync database list and selection
        ToolbarDatabaseCombo.ItemsSource = vm.Databases;
        ToolbarDatabaseCombo.SelectedItem = vm.SelectedDatabase;

        // Sync Run/Stop enabled state
        ToolbarRunButton.IsEnabled = !vm.IsRunning;
        ToolbarStopButton.IsEnabled = vm.IsRunning;

        // Listen for changes on this VM
        vm.PropertyChanged -= OnActiveTabPropertyChanged;
        vm.PropertyChanged += OnActiveTabPropertyChanged;

        // Wire caret position tracking on active editor (unsubscribe previous to prevent leak)
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
        // Fire immediately for current position
        CaretPositionChanged?.Invoke(
            activeTab.Editor.TextArea.Caret.Line,
            activeTab.Editor.TextArea.Caret.Column);
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender != ActiveTabViewModel) return;
        var vm = (QueryTabViewModel)sender;
        if (e.PropertyName == nameof(QueryTabViewModel.IsRunning))
        {
            ToolbarRunButton.IsEnabled = !vm.IsRunning;
            ToolbarStopButton.IsEnabled = vm.IsRunning;
        }
        else if (e.PropertyName == nameof(QueryTabViewModel.SelectedDatabase))
        {
            if (ToolbarDatabaseCombo.SelectedItem as string != vm.SelectedDatabase)
                ToolbarDatabaseCombo.SelectedItem = vm.SelectedDatabase;
        }
        else if (e.PropertyName == nameof(QueryTabViewModel.Databases))
        {
            ToolbarDatabaseCombo.ItemsSource = vm.Databases;
            ToolbarDatabaseCombo.SelectedItem = vm.SelectedDatabase;
        }
    }

    private async void UpdateObjectExplorerForTab(QueryTabViewModel tabVm)
    {
        if (_viewModel == null) return;
        var connStr = tabVm.TabConnectionString;
        _viewModel.ObjectExplorer.SetActiveConnection(connStr);

        // Check cache for this server's OE tree
        if (connStr != null && _serverCache.TryGetValue(connStr, out var cached) && cached.OeRootNodes.Count > 0)
        {
            _viewModel.ObjectExplorer.RestoreNodes(cached.OeRootNodes);
        }
        else
        {
            // Load databases for this server (from cache or fresh)
            try
            {
                var dbs = connStr != null && connStr != _primaryConnectionString
                    ? (_serverCache.TryGetValue(connStr, out var c) && c.Databases.Count > 0
                        ? c.Databases
                        : await _db!.GetDatabasesAsync(connStr))
                    : _cachedDatabases;
                await _viewModel.ObjectExplorer.LoadDatabasesAsync(dbs);
            }
            catch
            {
                // Connection might not be reachable
            }
        }
    }

    private static IBrush FindBrush(string key) => ThemeManager.GetBrush(key);

    // ── Tab drag-to-reorder ──────────────────────────────────────────

    private int FindTabIndexFromPointer(PointerEventArgs e)
    {
        for (int i = 0; i < _tabs.Count && i < TabStrip.Children.Count; i++)
        {
            if (TabStrip.Children[i] is Button btn)
            {
                var pos = e.GetPosition(btn);
                if (pos.X >= 0 && pos.X <= btn.Bounds.Width && pos.Y >= 0 && pos.Y <= btn.Bounds.Height)
                    return i;
            }
        }
        return -1;
    }

    private void OnTabStripPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        TabScrollViewer.Offset = TabScrollViewer.Offset.WithX(TabScrollViewer.Offset.X - delta * 60);
        e.Handled = true;
        UpdateTabOverflowArrows();
    }

    private void UpdateTabOverflowArrows()
    {
        var sv = TabScrollViewer;
        var canScrollLeft = sv.Offset.X > 1;
        var canScrollRight = sv.Extent.Width - sv.Offset.X - sv.Viewport.Width > 1;
        TabScrollLeftButton.IsVisible = canScrollLeft;
        TabScrollRightButton.IsVisible = canScrollRight;
    }

    private void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabStrip).Properties.IsLeftButtonPressed) return;
        var idx = FindTabIndexFromPointer(e);
        if (idx >= 0)
        {
            _tabDragIndex = idx;
            _tabDragStart = e.GetPosition(TabStrip);
            _tabDragging = false;
        }
    }

    private void OnTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragIndex < 0) return;
        var pos = e.GetPosition(TabStrip);
        if (!_tabDragging && Math.Abs(pos.X - _tabDragStart.X) > 12)
            _tabDragging = true;
        if (!_tabDragging) return;

        // Find which tab position we've dragged over
        var targetIdx = FindTabIndexFromPointer(e);
        if (targetIdx < 0 || targetIdx == _tabDragIndex) return;

        // Reorder: move the dragged tab to the target position
        var tab = _tabs[_tabDragIndex];
        _tabs.RemoveAt(_tabDragIndex);
        _tabs.Insert(targetIdx, tab);

        // Update active tab index to follow the active tab
        if (_activeTabIndex == _tabDragIndex)
            _activeTabIndex = targetIdx;
        else if (_tabDragIndex < _activeTabIndex && targetIdx >= _activeTabIndex)
            _activeTabIndex--;
        else if (_tabDragIndex > _activeTabIndex && targetIdx <= _activeTabIndex)
            _activeTabIndex++;

        _tabDragIndex = targetIdx;
        RebuildTabStrip();
    }

    private void OnTabStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _tabDragIndex = -1;
        _tabDragging = false;
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();

        var activeBg = FindBrush("ActiveTabBackground");
        var normalBg = Brushes.Transparent;
        var activeFg = FindBrush("TextBright");
        var normalFg = FindBrush("TextSecondary");
        var closeFg = FindBrush("TextSecondary");

        for (int i = 0; i < _tabs.Count; i++)
        {
            var idx = i;
            var vm = _tabs[i].DataContext as QueryTabViewModel;
            var title = vm?.TabTitle ?? $"Query {i + 1}";
            var isActive = i == _activeTabIndex;

            var headerPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };

            headerPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            // Check if tab's connection is active
            var tabConnId = vm?.TabConnectionProfile?.Id;
            var isTabConnected = tabConnId == null || _registry == null ||
                (_registry.GetById(tabConnId)?.IsConnected ?? false);

            // Connection color dot (faded if disconnected) + reconnect button
            if (vm?.TabConnectionProfile != null)
            {
                var dotColor = Avalonia.Media.Color.Parse(vm.TabConnectionColor);
                headerPanel.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new Avalonia.Media.SolidColorBrush(dotColor),
                    Opacity = isTabConnected ? 1.0 : 0.5,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });

                if (!isTabConnected && tabConnId != null)
                {
                    var reconnIdx = idx;
                    var reconnBtn = new Button
                    {
                        Content = "\u21bb", // ↻
                        Padding = new Thickness(1, 0),
                        Background = Brushes.Transparent,
                        Foreground = FindBrush("AccentBlue"),
                        FontSize = 11,
                        Cursor = new Cursor(StandardCursorType.Hand),
                        MinWidth = 0, MinHeight = 0,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        BorderThickness = new Thickness(0)
                    };
                    ToolTip.SetTip(reconnBtn, "Reconnect");
                    var capturedConnId = tabConnId;
                    reconnBtn.Click += async (_, _) =>
                    {
                        var (success, _) = await _registry!.ConnectAsync(capturedConnId);
                        if (success)
                        {
                            var managed = _registry.GetById(capturedConnId);
                            if (managed?.ResolvedConnectionString != null && reconnIdx < _tabs.Count)
                            {
                                var tabVm = _tabs[reconnIdx].DataContext as QueryTabViewModel;
                                if (tabVm != null)
                                {
                                    tabVm.TabConnectionString = managed.ResolvedConnectionString;
                                    _ = LoadDatabasesForTabAsync(tabVm, managed.ResolvedConnectionString);
                                }
                            }
                            RebuildTabStrip();
                            ActiveTabChanged?.Invoke();
                        }
                    };
                    headerPanel.Children.Add(reconnBtn);
                }
            }

            // Close button (×)
            var closeBtn = new Button
            {
                Content = "\u00d7",
                Padding = new Thickness(2, 0),
                Background = Brushes.Transparent,
                Foreground = closeFg,
                FontSize = 12,
                Cursor = new Cursor(StandardCursorType.Hand),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            closeBtn.Click += async (_, _) => await CloseTabAsync(idx);
            headerPanel.Children.Add(closeBtn);

            // Colored bottom border for connection environment (faded if disconnected)
            IBrush? bottomBorder = null;
            if (vm?.TabConnectionProfile != null)
            {
                var borderColor = Avalonia.Media.Color.Parse(vm.TabConnectionColor);
                bottomBorder = new Avalonia.Media.SolidColorBrush(borderColor,
                    isTabConnected ? 1.0 : 0.5);
            }

            // Tooltip: connection name + database
            var tooltipText = vm?.TabConnectionProfile != null
                ? $"{vm.TabConnectionDisplay}" + (vm.SelectedDatabase != null ? $" / {vm.SelectedDatabase}" : "")
                  + (!isTabConnected ? " (disconnected)" : "")
                : null;

            var tabBtn = new Button
            {
                Content = headerPanel,
                Background = isActive ? activeBg : normalBg,
                Foreground = isActive ? activeFg : normalFg,
                Padding = new Thickness(10, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = bottomBorder != null ? new Thickness(0, 0, 0, 2) : new Thickness(0),
                BorderBrush = bottomBorder
            };
            if (tooltipText != null)
                ToolTip.SetTip(tabBtn, tooltipText);
            tabBtn.Click += (_, _) => SwitchToTab(idx);

            // Middle-click to close
            tabBtn.PointerPressed += async (_, e) =>
            {
                if (e.GetCurrentPoint(tabBtn).Properties.IsMiddleButtonPressed)
                    await CloseTabAsync(idx);
            };

            // Right-click context menu
            tabBtn.ContextMenu = BuildTabContextMenu(idx);

            TabStrip.Children.Add(tabBtn);
        }

        // "+" button
        var addBtn = new Button
        {
            Content = "+",
            Background = Brushes.Transparent,
            Foreground = FindBrush("TextSecondary"),
            Padding = new Thickness(8, 4),
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(addBtn, "New Query (Ctrl+N)");
        addBtn.Click += (_, _) => AddNewTab();
        TabStrip.Children.Add(addBtn);

        // Update overflow arrows after layout
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTabOverflowArrows(), Avalonia.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Lightweight update of just the tab button text — avoids full RebuildTabStrip.
    /// Used for elapsed time ticks during query execution.
    /// </summary>
    private void UpdateTabButtonText(QueryTabViewModel vm)
    {
        var tabIdx = -1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].DataContext == vm) { tabIdx = i; break; }
        }
        if (tabIdx < 0 || tabIdx >= TabStrip.Children.Count) return;

        // Navigate: TabStrip → Button → StackPanel (Content) → first TextBlock
        if (TabStrip.Children[tabIdx] is Button btn &&
            btn.Content is StackPanel panel &&
            panel.Children.Count > 0 &&
            panel.Children[0] is TextBlock tb)
        {
            tb.Text = vm.TabTitle;
        }
    }

    private ContextMenu BuildTabContextMenu(int tabIndex)
    {
        var menu = new ContextMenu();

        var targetTab = _tabs[tabIndex];

        var close = new MenuItem { Header = "Close" };
        close.Click += async (_, _) =>
        {
            var idx = _tabs.IndexOf(targetTab);
            if (idx >= 0) await CloseTabAsync(idx);
        };
        menu.Items.Add(close);

        var closeOthers = new MenuItem { Header = "Close Others" };
        closeOthers.Click += async (_, _) =>
        {
            var tabsToClose = _tabs.Where(t => t != targetTab).ToList();
            foreach (var tab in tabsToClose)
            {
                var countBefore = _tabs.Count;
                var idx = _tabs.IndexOf(tab);
                if (idx >= 0) await CloseTabAsync(idx);
                if (_tabs.Count == countBefore) break; // User cancelled — abort batch
            }
        };
        menu.Items.Add(closeOthers);

        var closeRight = new MenuItem { Header = "Close Tabs to the Right" };
        closeRight.Click += async (_, _) =>
        {
            var idx = _tabs.IndexOf(targetTab);
            if (idx < 0) return;
            var tabsToClose = _tabs.Skip(idx + 1).ToList();
            foreach (var tab in tabsToClose)
            {
                var countBefore = _tabs.Count;
                var i = _tabs.IndexOf(tab);
                if (i >= 0) await CloseTabAsync(i);
                if (_tabs.Count == countBefore) break; // User cancelled — abort batch
            }
        };
        menu.Items.Add(closeRight);

        var closeAll = new MenuItem { Header = "Close All" };
        closeAll.Click += async (_, _) =>
        {
            var tabsToClose = _tabs.ToList();
            foreach (var tab in tabsToClose)
            {
                var countBefore = _tabs.Count;
                var idx = _tabs.IndexOf(tab);
                if (idx >= 0) await CloseTabAsync(idx);
                if (_tabs.Count == countBefore) break; // User cancelled — abort batch
            }
        };
        menu.Items.Add(closeAll);

        menu.Items.Add(new Separator());

        var duplicate = new MenuItem { Header = "Duplicate Tab" };
        duplicate.Click += (_, _) => DuplicateTab(tabIndex);
        menu.Items.Add(duplicate);

        // Reconnect option for disconnected tabs
        var tabVm = _tabs[tabIndex].DataContext as QueryTabViewModel;
        var tabConnId = tabVm?.TabConnectionProfile?.Id;
        if (tabConnId != null && _registry != null)
        {
            var managed = _registry.GetById(tabConnId);
            if (managed != null && !managed.IsConnected)
            {
                menu.Items.Add(new Separator());
                var reconnect = new MenuItem { Header = "Reconnect" };
                reconnect.Click += async (_, _) =>
                {
                    var (success, error) = await _registry.ConnectAsync(tabConnId);
                    if (success && managed.ResolvedConnectionString != null)
                    {
                        tabVm!.TabConnectionString = managed.ResolvedConnectionString;
                        _ = LoadDatabasesForTabAsync(tabVm, managed.ResolvedConnectionString);
                        RebuildTabStrip();
                        ActiveTabChanged?.Invoke();
                    }
                };
                menu.Items.Add(reconnect);
            }
        }

        return menu;
    }

    public void DuplicateTab(int sourceIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= _tabs.Count) return;
        var sourceVm = _tabs[sourceIndex].DataContext as QueryTabViewModel;
        if (sourceVm == null) return;

        AddNewTab(sourceVm.TabConnectionString, sourceVm.TabConnectionProfile);

        // Copy SQL and database from source
        var newTab = _tabs[^1];
        var newVm = newTab.DataContext as QueryTabViewModel;
        if (newVm != null)
        {
            // Load databases with desired selection (avoids race condition)
            var connStr = newVm.TabConnectionString;
            if (connStr != null && sourceVm.SelectedDatabase != null)
                _ = LoadDatabasesForTabAsync(newVm, connStr, sourceVm.SelectedDatabase);

            newTab.Editor.Text = _tabs[sourceIndex].Editor.Text;
            newVm.SqlText = sourceVm.SqlText;
        }
    }
}
