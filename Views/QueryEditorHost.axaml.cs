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
    private ConnectionRegistry? _registry;
    private SessionService? _sessionService;
    private readonly List<QueryTabView> _tabs = [];
    private int _activeTabIndex = -1;
    private int _tabCounter;
    private int _tabDragIndex = -1;
    private Point _tabDragStart;
    private bool _tabDragging;
    private List<string> _cachedDatabases = [];
    private bool _restoringSession;
    private Timer? _autosaveTimer;

    // Per-server caching (v1.6.0)
    private readonly Dictionary<string, CachedServerData> _serverCache = new();
    private string? _primaryConnectionString;
    private SavedConnection? _primaryProfile;

    // Intellisense cache: key = "connectionString|database"
    private readonly Dictionary<string, IntellisenseService> _intellisenseCache = new(StringComparer.OrdinalIgnoreCase);

    // Panel collapse
    private SettingsService? _settings;
    private bool _oeCollapsed;

    // Caret tracking (prevent handler leak on tab switch)
    private AvaloniaEdit.TextEditor? _lastCaretEditor;
    private EventHandler? _caretHandler;

    private class CachedServerData
    {
        public List<string> Databases { get; set; } = [];
        public List<ObjectExplorerNode> OeRootNodes { get; set; } = [];
    }

    /// <summary>Get the active query tab's ViewModel (for status bar binding).</summary>
    public QueryTabViewModel? ActiveTabViewModel =>
        _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            ? _tabs[_activeTabIndex].DataContext as QueryTabViewModel
            : null;

    /// <summary>Fired when the active query tab changes (for status bar rebinding).</summary>
    public event Action? ActiveTabChanged;

    /// <summary>Fired when cursor position changes in the active editor. (line, column)</summary>
    public event Action<int, int>? CaretPositionChanged;

    /// <summary>Fired when user requests "New Connection" from OE empty-space context menu.</summary>
    public event Action? NewConnectionRequested;

    /// <summary>Fired after session restore if some tabs couldn't reconnect.</summary>
    public event Action<string>? SessionRestoreWarning;

    public QueryEditorHost()
    {
        InitializeComponent();
    }

    public void RefreshTheme()
    {
        foreach (var tab in _tabs)
            tab.RefreshTheme();
        RebuildTabStrip();
    }

    public void Initialize(DatabaseService db, MainWindowViewModel mainVm, SessionService sessionService, SettingsService settings,
        ConnectionRegistry? registry = null)
    {
        _db = db;
        _registry = registry;
        _sessionService = sessionService;
        _settings = settings;
        _viewModel = new QueryEditorHostViewModel(db, registry);
        DataContext = _viewModel;

        // Wire Object Explorer events → active tab
        _viewModel.ObjectExplorer.InsertTextRequested += (sql, autoRun, dbName, connId) => OnInsertText(sql, autoRun, dbName, connId);
        _viewModel.ObjectExplorer.InsertAtCursorRequested += OnInsertAtCursor;
        _viewModel.ObjectExplorer.EditDataRequested += (sql, dbName, connId) => OnEditDataRequested(sql, dbName, connId);
        _viewModel.ObjectExplorer.CopyToClipboardRequested += OnCopyToClipboard;
        _viewModel.ObjectExplorer.AlterSequenceRequested += OnAlterSequenceRequested;
        _viewModel.ObjectExplorer.ResetSequenceRequested += OnResetSequenceRequested;
        _viewModel.ObjectExplorer.StartJobRequested += OnStartJobRequested;

        // Rebuild tab strip when connection state changes (dot fade/unfade)
        if (_registry != null)
            _registry.ConnectionStateChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(RebuildTabStrip);

        // Wire tree interactions
        ObjectExplorerTree.AddHandler(InputElement.DoubleTappedEvent, OnTreeDoubleTapped, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerMovedEvent, OnTreePointerMoved, handledEventsToo: true);
        ObjectExplorerTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, handledEventsToo: true);

        // Refresh query tab strip on theme change
        ThemeManager.ThemeChanged += RefreshTheme;

        // Tab drag-to-reorder (handled at TabStrip level, doesn't interfere with button Click)
        TabStrip.AddHandler(InputElement.PointerPressedEvent, OnTabStripPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TabStrip.AddHandler(InputElement.PointerMovedEvent, OnTabStripPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TabStrip.AddHandler(InputElement.PointerReleasedEvent, OnTabStripPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Mouse wheel scrolls the tab strip horizontally
        TabStrip.AddHandler(InputElement.PointerWheelChangedEvent, OnTabStripPointerWheel);

        // Tab overflow arrows
        TabScrollLeftButton.Click += (_, _) => { TabScrollViewer.Offset = TabScrollViewer.Offset.WithX(Math.Max(0, TabScrollViewer.Offset.X - 120)); UpdateTabOverflowArrows(); };
        TabScrollRightButton.Click += (_, _) => { TabScrollViewer.Offset = TabScrollViewer.Offset.WithX(TabScrollViewer.Offset.X + 120); UpdateTabOverflowArrows(); };
        TabScrollViewer.ScrollChanged += (_, _) => UpdateTabOverflowArrows();

        // Wire history button (toggle panel)
        QueryHistoryButton.Click += (_, _) => ToggleHistoryPanel();
        HistoryCloseButton.Click += (_, _) => ToggleHistoryPanel();
        HistorySearchBox.TextChanged += (_, _) => RefreshHistoryGrid();
        HistoryGrid.DoubleTapped += OnHistoryGridDoubleTapped;

        // Wire autocomplete toggle
        AutocompleteToggleButton.Click += OnAutocompleteToggleClicked;
        UpdateAutocompleteToggleVisual();

        // Wire Quick Quote button
        QuickQuoteButton.Click += (_, _) => QuickQuoteSelection(nPrefix: false);

        // Wire Format button
        FormatButton.Click += (_, _) => FormatSqlInEditor();

        // Wire OE refresh/collapse/expand buttons
        OeRefreshButton.Click += async (_, _) => await ReloadDatabasesAsync();
        OeCollapseButton.Click += (_, _) => ToggleObjectExplorer();
        OeExpandButton.Click += (_, _) => ToggleObjectExplorer();
        RestoreObjectExplorerState();

        // Wire merged toolbar buttons (Run/Stop → active tab VM)
        ToolbarRunButton.Click += (_, _) =>
        {
            var vm = ActiveTabViewModel;
            if (vm == null) return;
            var editor = GetActiveEditor();
            vm.SelectedSqlText = editor?.SelectedText ?? "";
            vm.SqlText = editor?.Text ?? "";
            if (vm.RunQueryCommand.CanExecute(null))
                _ = vm.RunQueryCommand.ExecuteAsync(null);
        };
        ToolbarStopButton.Click += (_, _) =>
        {
            var vm = ActiveTabViewModel;
            if (vm?.StopQueryCommand.CanExecute(null) == true)
                vm.StopQueryCommand.Execute(null);
        };
        ToolbarTraceButton.Click += (_, _) =>
        {
            var vm = ActiveTabViewModel;
            if (vm == null) return;
            var editor = GetActiveEditor();
            vm.SelectedSqlText = editor?.SelectedText ?? "";
            vm.SqlText = editor?.Text ?? "";
            if (vm.RunWithTraceCommand.CanExecute(null))
                _ = vm.RunWithTraceCommand.ExecuteAsync(null);
        };
        ToolbarDatabaseCombo.SelectionChanged += (_, _) =>
        {
            var vm = ActiveTabViewModel;
            if (vm != null && ToolbarDatabaseCombo.SelectedItem is string db && db != vm.SelectedDatabase)
                vm.SelectedDatabase = db;
        };

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

    /// <summary>
    /// Called from MainWindow after Connection Dialog — sets the default connection for new tabs.
    /// </summary>
    public void SetDefaultConnection(ConnectionSettings settings, SavedConnection? profile)
    {
        _primaryConnectionString = settings.ConnectionString;
        _primaryProfile = profile;

        // Set on all existing tabs that don't have their own connection
        foreach (var tab in _tabs)
        {
            if (tab.DataContext is QueryTabViewModel vm && vm.TabConnectionString == null)
            {
                vm.TabConnectionString = settings.ConnectionString;
                // Don't override profile if it was restored from a different connection (e.g. disconnected DEV tab)
                if (vm.TabConnectionProfile == null)
                    vm.TabConnectionProfile = profile;
            }
        }
    }

    public void ClearServerCaches() => _serverCache.Clear();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _autosaveTimer?.Dispose();
        _autosaveTimer = null;
        base.OnDetachedFromVisualTree(e);
    }
}
