using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaloniaEdit;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost : UserControl
{
    private QueryEditorHostViewModel? _viewModel;
    private DatabaseService? _db;
    private SessionService? _sessionService;
    private readonly List<QueryTabView> _tabs = [];
    private int _activeTabIndex = -1;
    private int _tabCounter;
    private List<string> _cachedDatabases = [];
    private bool _restoringSession;
    private Timer? _autosaveTimer;

    /// <summary>Get the active query tab's ViewModel (for status bar binding).</summary>
    public QueryTabViewModel? ActiveTabViewModel =>
        _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            ? _tabs[_activeTabIndex].DataContext as QueryTabViewModel
            : null;

    /// <summary>Fired when the active query tab changes (for status bar rebinding).</summary>
    public event Action? ActiveTabChanged;

    public QueryEditorHost()
    {
        InitializeComponent();
    }

    public void Initialize(DatabaseService db, MainWindowViewModel mainVm, SessionService sessionService)
    {
        _db = db;
        _sessionService = sessionService;
        _viewModel = new QueryEditorHostViewModel(db);
        DataContext = _viewModel;

        // Wire Object Explorer events → active tab
        _viewModel.ObjectExplorer.InsertTextRequested += OnInsertText;
        _viewModel.ObjectExplorer.InsertAtCursorRequested += OnInsertAtCursor;
        _viewModel.ObjectExplorer.EditDataRequested += OnEditDataRequested;

        // Wire tree interactions
        ObjectExplorerTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, handledEventsToo: true);

        // Wire history button
        QueryHistoryButton.Click += OnHistoryButtonClicked;

        // Restore session or create default tab
        RestoreSession();

        // Load databases if already connected
        if (mainVm.IsConnected)
            _ = ReloadDatabasesAsync();

        // Start autosave timer (30s interval)
        _autosaveTimer = new Timer(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(SaveSession),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _autosaveTimer?.Dispose();
        _autosaveTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ── Tab Lifecycle ────────────────────────────────────────────────

    public void AddNewTab()
    {
        if (_db == null) return;

        _tabCounter++;
        var vm = new QueryTabViewModel(_db)
        {
            TabTitle = $"Query {_tabCounter}"
        };

        // Inherit databases + selected DB from active tab
        if (_cachedDatabases.Count > 0)
        {
            var selectedDb = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                ? _tabs[_activeTabIndex].DataContext is QueryTabViewModel activeVm
                    ? activeVm.SelectedDatabase
                    : null
                : null;
            vm.SetDatabases(_cachedDatabases, selectedDb);
        }

        // Refresh tab strip when unsaved changes indicator changes
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueryTabViewModel.HasUnsavedChanges) or nameof(QueryTabViewModel.TabTitle))
                RebuildTabStrip();
        };

        // Record query history on successful execution
        vm.QueryExecuted += (sql, db, rows) => _sessionService?.AddQueryToHistory(sql, db, rows);

        var tabView = new QueryTabView();
        tabView.Initialize(vm);
        tabView.ProcDropRequested += OnProcDropRequested;

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

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var changed = _activeTabIndex != index;
        _activeTabIndex = index;

        // Show/hide tab views
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsVisible = i == index;

        RebuildTabStrip();
        ActiveTabChanged?.Invoke();

        // Save session on tab switch
        if (changed && !_restoringSession)
            SaveSession();
    }

    private IBrush FindBrush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Transparent;

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
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });

            // Close button (×)
            var closeBtn = new Button
            {
                Content = "\u00d7",
                Padding = new Thickness(2, 0),
                Background = Brushes.Transparent,
                Foreground = closeFg,
                FontSize = 14,
                Cursor = new Cursor(StandardCursorType.Hand),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            closeBtn.Click += async (_, _) => await CloseTabAsync(idx);
            headerPanel.Children.Add(closeBtn);

            var tabBtn = new Button
            {
                Content = headerPanel,
                Background = isActive ? activeBg : normalBg,
                Foreground = isActive ? activeFg : normalFg,
                Padding = new Thickness(12, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0)
            };
            tabBtn.Click += (_, _) => SwitchToTab(idx);

            // Middle-click to close
            tabBtn.PointerPressed += async (_, e) =>
            {
                if (e.GetCurrentPoint(tabBtn).Properties.IsMiddleButtonPressed)
                    await CloseTabAsync(idx);
            };

            TabStrip.Children.Add(tabBtn);
        }

        // "+" button
        var addBtn = new Button
        {
            Content = "+",
            Background = Brushes.Transparent,
            Foreground = FindBrush("TextSecondary"),
            Padding = new Thickness(10, 6),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(addBtn, "New Query (Ctrl+N)");
        addBtn.Click += (_, _) => AddNewTab();
        TabStrip.Children.Add(addBtn);
    }

    // ── Public API (for MainWindow) ──────────────────────────────────

    /// <summary>
    /// Handle F5 / Ctrl+Enter by delegating to active tab.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        return _tabs[_activeTabIndex].HandleKeyDown(e);
    }

    /// <summary>
    /// Run the query in the active tab.
    /// </summary>
    public void RunActiveQuery()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm != null)
        {
            vm.SelectedSqlText = tab.Editor.SelectedText ?? "";
            vm.SqlText = tab.Editor.Text;
            if (vm.RunQueryCommand.CanExecute(null))
                _ = vm.RunQueryCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Stop the query in the active tab.
    /// </summary>
    public void StopActiveQuery()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var vm = _tabs[_activeTabIndex].DataContext as QueryTabViewModel;
        if (vm?.StopQueryCommand.CanExecute(null) == true)
            vm.StopQueryCommand.Execute(null);
    }

    /// <summary>
    /// Get the active tab's TextEditor (for Edit menu pass-through).
    /// </summary>
    public TextEditor? GetActiveEditor()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return null;
        return _tabs[_activeTabIndex].Editor;
    }

    public void FocusActiveDatabasePicker()
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].FocusDatabasePicker();
    }

    /// <summary>
    /// Reload databases into Object Explorer and all tabs.
    /// </summary>
    public async Task ReloadDatabasesAsync()
    {
        if (_db == null || _viewModel == null) return;

        try
        {
            var dbs = await _db.GetDatabasesAsync();
            _cachedDatabases = new List<string>(dbs);

            // Update Object Explorer
            await _viewModel.ObjectExplorer.LoadDatabasesAsync(dbs);

            // Update all tabs
            foreach (var tab in _tabs)
            {
                if (tab.DataContext is QueryTabViewModel vm)
                    vm.SetDatabases(dbs, vm.SelectedDatabase);
            }
        }
        catch
        {
            // Connection might not be ready yet
        }
    }

    private SettingsService? GetSettingsService()
    {
        var mainWindow = TopLevel.GetTopLevel(this) as MainWindow;
        return mainWindow?.AppSettings;
    }

    // ── Save / Open Public API (for MainWindow) ──────────────────────

    /// <summary>
    /// Save active query. If no path yet, shows SaveQueryDialog first.
    /// Returns true if saved successfully.
    /// </summary>
    public async Task<bool> SaveActiveQueryAsync(QueryFileService svc, SettingsService settings)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm == null) return false;

        // Sync text from editor
        vm.SqlText = tab.Editor.Text;

        if (vm.CurrentQueryPath != null)
        {
            vm.Save(svc, settings);
            RebuildTabStrip();
            return true;
        }

        // No path yet — show Save As dialog
        return await SaveAsActiveQueryAsync(svc, settings);
    }

    /// <summary>
    /// Always shows native Save File dialog, then saves.
    /// </summary>
    public async Task<bool> SaveAsActiveQueryAsync(QueryFileService svc, SettingsService settings)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm == null) return false;

        // Sync text from editor
        vm.SqlText = tab.Editor.Text;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return false;

        var defaultName = vm.CurrentQueryName ?? "query";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save Query",
            SuggestedFileName = defaultName,
            DefaultExtension = "sql",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("SQL Files") { Patterns = ["*.sql"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (file == null) return false;

        var path = file.TryGetLocalPath();
        if (path == null) return false;

        vm.CurrentQueryPath = path;
        vm.CurrentQueryName = Path.GetFileNameWithoutExtension(path);
        vm.Save(svc, settings);
        RebuildTabStrip();
        return true;
    }

    /// <summary>
    /// Shows native Open File dialog, creates a new tab and loads the selected file.
    /// </summary>
    public async Task OpenQueryAsync(QueryFileService svc, SettingsService settings)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open SQL File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("SQL Files") { Patterns = ["*.sql"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path == null) return;

        OpenQueryFromPath(path, svc, settings);
    }

    /// <summary>
    /// Open a query file directly (for Recent Files menu).
    /// </summary>
    public void OpenQueryFromPath(string path, QueryFileService svc, SettingsService settings)
    {
        if (!File.Exists(path)) return;

        AddNewTab();
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            var vm = tab.DataContext as QueryTabViewModel;
            if (vm != null)
            {
                vm.LoadFromFile(path, svc, settings);
                // Update editor text to match loaded SQL
                tab.Editor.Text = vm.SqlText;
                RebuildTabStrip();
            }
        }
    }

    // ── Session Save / Restore ─────────────────────────────────────

    /// <summary>
    /// Save current tab state to session file. Called on tab create, close, switch, and app close.
    /// </summary>
    public void SaveSession()
    {
        if (_sessionService == null) return;

        var tabs = new List<TabState>();
        foreach (var tabView in _tabs)
        {
            if (tabView.DataContext is not QueryTabViewModel vm) continue;

            // Sync latest editor text
            vm.SqlText = tabView.Editor.Text;

            tabs.Add(new TabState
            {
                SqlText = vm.SqlText,
                SelectedDatabase = vm.SelectedDatabase,
                SavedPath = vm.CurrentQueryPath,
                QueryName = vm.CurrentQueryName,
                CursorPosition = tabView.Editor.CaretOffset
            });
        }

        _sessionService.SaveTabs(tabs, _activeTabIndex);
    }

    private void RestoreSession()
    {
        if (_sessionService == null)
        {
            AddNewTab();
            return;
        }

        var data = _sessionService.Data;
        if (data.Tabs.Count == 0)
        {
            AddNewTab();
            return;
        }

        _restoringSession = true;
        try
        {
            foreach (var tabState in data.Tabs)
            {
                AddNewTab();
                if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) continue;

                var tabView = _tabs[_activeTabIndex];
                var vm = tabView.DataContext as QueryTabViewModel;
                if (vm == null) continue;

                // Restore saved query metadata
                if (tabState.SavedPath != null)
                {
                    vm.CurrentQueryPath = tabState.SavedPath;
                    vm.CurrentQueryName = tabState.QueryName;
                    if (tabState.QueryName != null)
                        vm.TabTitle = tabState.QueryName;
                }

                // Restore database selection (will be applied when databases load)
                if (tabState.SelectedDatabase != null)
                    vm.SelectedDatabase = tabState.SelectedDatabase;

                // Restore editor text
                tabView.Editor.Text = tabState.SqlText;
                vm.SetInitialText(tabState.SqlText);

                // Restore cursor position
                if (tabState.CursorPosition >= 0 && tabState.CursorPosition <= tabState.SqlText.Length)
                    tabView.Editor.CaretOffset = tabState.CursorPosition;

                RebuildTabStrip();
            }

            // Restore active tab
            var targetIndex = Math.Clamp(data.ActiveTabIndex, 0, _tabs.Count - 1);
            SwitchToTab(targetIndex);
        }
        finally
        {
            _restoringSession = false;
        }
    }

    // ── Query History ────────────────────────────────────────────────

    private void OnHistoryButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sessionService == null) return;

        var history = _sessionService.GetQueryHistory();
        var menu = new MenuFlyout();

        if (history.Count == 0)
        {
            var empty = new MenuItem { Header = "(no history)", IsEnabled = false };
            menu.Items.Add(empty);
        }
        else
        {
            foreach (var entry in history)
            {
                var truncated = entry.SqlText.ReplaceLineEndings(" ");
                if (truncated.Length > 80)
                    truncated = truncated[..77] + "...";

                var timeAgo = FormatTimeAgo(entry.ExecutedAt);
                var dbLabel = entry.Database ?? "";

                var item = new MenuItem
                {
                    Header = truncated
                };
                ToolTip.SetTip(item, $"{entry.SqlText}\n\n{dbLabel} — {timeAgo} — {entry.RowCount:N0} rows");

                var sql = entry.SqlText;
                var db = entry.Database;
                item.Click += (_, _) =>
                {
                    AddNewTab();
                    if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
                    {
                        var tab = _tabs[_activeTabIndex];
                        var vm = tab.DataContext as QueryTabViewModel;
                        if (vm != null)
                        {
                            tab.Editor.Text = sql;
                            vm.SetInitialText(sql);
                            if (db != null && vm.Databases.Contains(db))
                                vm.SelectedDatabase = db;
                        }
                    }
                };
                menu.Items.Add(item);
            }
        }

        menu.ShowAt(QueryHistoryButton, true);
    }

    private static string FormatTimeAgo(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return when.ToString("MMM d");
    }

    // ── Object Explorer Event Routing ────────────────────────────────

    private void OnInsertText(string sql, bool autoRun)
    {
        // Always open context menu / Object Explorer actions in a new tab
        AddNewTab();
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertText(sql, autoRun);
    }

    private void OnInsertAtCursor(string text)
    {
        // Column insert stays in current tab (user is building a query)
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertAtCursor(text);
    }

    private void OnProcDropRequested(ObjectExplorerNode node)
    {
        if (_viewModel == null) return;
        // ViewDefinitionAsync fires InsertTextRequested → OnInsertText → new tab
        _ = _viewModel.ObjectExplorer.ViewDefinitionAsync(node);
    }

    private void OnEditDataRequested(string sql)
    {
        AddNewTab();
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            var vm = tab.DataContext as QueryTabViewModel;
            if (vm != null)
                vm.AutoEnterEditMode = true;
            tab.InsertText(sql, autoRun: true);
        }
    }

    // ── Drag-and-Drop ─────────────────────────────────────────────────

    private Point _dragStartPoint;
    private bool _dragPending;
    private ObjectExplorerNode? _dragNode;
    private const double DragThreshold = 8;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ObjectExplorerTree).Properties.IsLeftButtonPressed &&
            e.Source is Visual visual)
        {
            var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
            if (treeViewItem?.DataContext is ObjectExplorerNode node &&
                node.NodeType is ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
                    or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
                    or ObjectExplorerNodeType.Column)
            {
                _dragStartPoint = e.GetPosition(ObjectExplorerTree);
                _dragNode = node;
                _dragPending = true;
                return;
            }
        }
        _dragPending = false;
        _dragNode = null;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragPending || _dragNode == null) return;

        var pos = e.GetPosition(ObjectExplorerTree);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragPending = false;
        var node = _dragNode;
        _dragNode = null;

#pragma warning disable CS0618 // DataObject/DoDragDrop obsolete
        var data = new DataObject();
        data.Set("ObjectExplorerNode", node);

        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
    }

    // ── Context Menu + Double-Click ──────────────────────────────────

    private void ShowContextMenu(ObjectExplorerNode node, Control target)
    {
        if (_viewModel == null) return;

        var explorer = _viewModel.ObjectExplorer;
        var menu = new MenuFlyout();

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Table:
                menu.Items.Add(CreateMenuItem("SELECT TOP 100", () => explorer.SelectTop100(node)));
                menu.Items.Add(CreateMenuItem("SELECT COUNT(*)", () => explorer.SelectCount(node)));
                menu.Items.Add(CreateMenuItem("Edit Data", () => explorer.EditData(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as CREATE", () => explorer.ScriptAsCreate(node)));
                break;

            case ObjectExplorerNodeType.View:
                menu.Items.Add(CreateMenuItem("SELECT TOP 100", () => explorer.SelectTop100(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                break;

            case ObjectExplorerNodeType.Proc:
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                menu.Items.Add(CreateMenuItem("Generate EXEC", () => _ = explorer.GenerateExecAsync(node)));
                break;

            case ObjectExplorerNodeType.Function:
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                break;

            case ObjectExplorerNodeType.Column:
                menu.Items.Add(CreateMenuItem("SELECT DISTINCT", () => explorer.SelectDistinct(node)));
                menu.Items.Add(CreateMenuItem("Insert Column Name", () => explorer.InsertColumnName(node)));
                break;

            case ObjectExplorerNodeType.Database:
            case ObjectExplorerNodeType.Folder:
                menu.Items.Add(CreateMenuItem("Refresh", () => explorer.RefreshNode(node)));
                break;

            default:
                return;
        }

        menu.ShowAt(target, true);
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Reset drag state on any button release — prevents drag from stealing double-click
        _dragPending = false;
        _dragNode = null;

        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (e.Source is not Visual visual) return;

        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem?.DataContext is not ObjectExplorerNode node) return;

        ShowContextMenu(node, treeViewItem);
        e.Handled = true;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null || e.Source is not Visual visual)
            return;

        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem?.DataContext is not ObjectExplorerNode node)
            return;

        var explorer = _viewModel.ObjectExplorer;

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Table:
                explorer.SelectTop100(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Proc:
                _ = explorer.ViewDefinitionAsync(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Column:
                explorer.InsertColumnName(node);
                e.Handled = true;
                break;
        }
    }
}
