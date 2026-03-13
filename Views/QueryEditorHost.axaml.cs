using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private readonly List<QueryTabView> _tabs = [];
    private int _activeTabIndex = -1;
    private int _tabCounter;
    private List<string> _cachedDatabases = [];

    public QueryEditorHost()
    {
        InitializeComponent();
    }

    public void Initialize(DatabaseService db, MainWindowViewModel mainVm)
    {
        _db = db;
        _viewModel = new QueryEditorHostViewModel(db);
        DataContext = _viewModel;

        // Wire Object Explorer events → active tab
        _viewModel.ObjectExplorer.InsertTextRequested += OnInsertText;
        _viewModel.ObjectExplorer.InsertAtCursorRequested += OnInsertAtCursor;

        // Wire tree interactions
        ObjectExplorerTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, handledEventsToo: true);

        // Create first tab
        AddNewTab();

        // Load databases if already connected
        if (mainVm.IsConnected)
            _ = ReloadDatabasesAsync();
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

        var tabView = new QueryTabView();
        tabView.Initialize(vm);

        _tabs.Add(tabView);
        TabContentPanel.Children.Add(tabView);

        SwitchToTab(_tabs.Count - 1);
    }

    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var tabView = _tabs[index];
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
    }

    public void CloseActiveTab()
    {
        if (_activeTabIndex >= 0)
            CloseTab(_activeTabIndex);
    }

    private void SwitchToTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        _activeTabIndex = index;

        // Show/hide tab views
        for (int i = 0; i < _tabs.Count; i++)
            _tabs[i].IsVisible = i == index;

        RebuildTabStrip();
    }

    private void RebuildTabStrip()
    {
        TabStrip.Children.Clear();

        var activeBg = new SolidColorBrush(Color.Parse("#2a2a4e"));
        var normalBg = Brushes.Transparent;
        var activeFg = Brushes.White;
        var normalFg = new SolidColorBrush(Color.Parse("#888888"));
        var closeFg = new SolidColorBrush(Color.Parse("#888888"));

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
            closeBtn.Click += (_, _) => CloseTab(idx);
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
            tabBtn.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(tabBtn).Properties.IsMiddleButtonPressed)
                    CloseTab(idx);
            };

            TabStrip.Children.Add(tabBtn);
        }

        // "+" button
        var addBtn = new Button
        {
            Content = "+",
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
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

    // ── Object Explorer Event Routing ────────────────────────────────

    private void OnInsertText(string sql, bool autoRun)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertText(sql, autoRun);
    }

    private void OnInsertAtCursor(string text)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertAtCursor(text);
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
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (e.Source is not Visual visual) return;

        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem?.DataContext is not ObjectExplorerNode node) return;

        ShowContextMenu(node, treeViewItem);
        e.Handled = true;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null || e.Source is not Visual visual) return;

        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem?.DataContext is not ObjectExplorerNode node) return;

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
