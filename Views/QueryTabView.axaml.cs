using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using SqlVersionControl.Converters;
using SqlVersionControl.Models;
using SqlVersionControl.Rendering;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryTabView : UserControl
{
    private QueryTabViewModel? _viewModel;
    private int _selectedTabIndex = -1;
    private CompletionWindow? _completionWindow;
    private IntellisenseService? _intellisenseService;
    private Func<bool>? _isAutocompleteEnabled;
    private SettingsService? _settings;
    private bool _resultsCollapsed = true; // Start collapsed — expand on first query result

    // Pinned result tabs — preserved across query re-runs
    private readonly List<(QueryResult Result, string Label)> _pinnedResults = [];
    private readonly HashSet<int> _pinnedTabIndices = []; // indices in the combined tab list that are pinned

    // Row state colors — resolved from AppTheme resources at runtime
    private IBrush GetRowBrush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Transparent;

    private IBrush ModifiedBrush => GetRowBrush("RowModified");
    private IBrush NewBrush => GetRowBrush("RowInserted");
    private IBrush DeletedBrush => GetRowBrush("RowDeleted");

    public QueryTabView()
    {
        InitializeComponent();
    }

    public TextEditor Editor => SqlEditor;

    public void FocusDatabasePicker()
    {
        // Database combo is now in QueryEditorHost toolbar — this is a no-op
    }

    public void SetIntellisenseService(IntellisenseService service)
    {
        _intellisenseService = service;
    }

    public void SetAutocompleteCheck(Func<bool> check)
    {
        _isAutocompleteEnabled = check;
    }

    public void Initialize(QueryTabViewModel vm, SettingsService? settings = null)
    {
        _viewModel = vm;
        _settings = settings;
        DataContext = vm;

        LoadSyntaxHighlighting();
        ConfigureEditor();
        ApplyGridRowHeight();
        ApplyEditorFontSize();
        ApplyEditorSelectionColors();

        ThemeManager.ThemeChanged += RefreshTheme;

        // Enable drag-and-drop on editor
        DragDrop.SetAllowDrop(SqlEditor, true);
        SqlEditor.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        SqlEditor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);

        // Cmd/Ctrl+Mouse Wheel to zoom
        SqlEditor.AddHandler(InputElement.PointerWheelChangedEvent, OnEditorPointerWheelChanged, handledEventsToo: false);

        _activeResultsGrid = ResultsGrid;
        vm.Results.CollectionChanged += OnResultsChanged;
        vm.ExpandResultsForMessages += () =>
        {
            // DML with no result sets — expand to show Messages tab
            if (_resultsCollapsed)
            {
                _resultsCollapsed = false;
                var totalHeight = EditorResultsGrid.Bounds.Height;
                var msgHeight = Math.Max(totalHeight > 0 ? totalHeight * 0.25 : 150, 80);
                EditorResultsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                EditorResultsGrid.RowDefinitions[2].Height = new GridLength(msgHeight, GridUnitType.Pixel);
                ResultsSplitter.IsEnabled = true;
                ResultsCollapseButton.Content = "\u25BC";
            }
            // Build tab bar (so Messages tab button exists) and switch to it
            RebuildResultTabs();
            SelectMessagesTab();
        };
        vm.ShowMessagesRequested += () => SelectMessagesTab();

        // Edit mode state changes
        vm.EditModeChanged += OnEditModeChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueryTabViewModel.PendingChangeCount))
                UpdateEditBar();
            if (e.PropertyName == nameof(QueryTabViewModel.IsEditMode))
                UpdateEditModeButton();
            if (e.PropertyName is nameof(QueryTabViewModel.TabConnectionString)
                or nameof(QueryTabViewModel.SelectedDatabase))
                UpdateEmptyStateText();
        };
        UpdateEmptyStateText();

        // Show SQL preview button + Export
        ShowSqlButton.Click += OnShowSqlClicked;
        ExportButton.Click += OnExportClicked;
        WireExportCancelButton();

        // DataGrid row events for edit mode
        ResultsGrid.LoadingRow += OnDataGridLoadingRow;
        ResultsGrid.RowEditEnded += OnDataGridRowEditEnded;

        // Double-click result grid to auto-enter edit mode
        ResultsGrid.DoubleTapped += OnResultsGridDoubleTapped;

        // Keyboard shortcuts on results grid (Ctrl+V paste in edit mode)
        ResultsGrid.AddHandler(KeyDownEvent, OnResultsGridKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Cell detail viewer — track both row changes and cell clicks
        ResultsGrid.SelectionChanged += OnResultsGridCellSelected;
        ResultsGrid.CellPointerPressed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateCellDetail(), Avalonia.Threading.DispatcherPriority.Background);
        CellDetailCopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(CellDetailText.Text ?? "");
        };
        CellDetailCloseButton.Click += (_, _) =>
        {
            CellDetailPanel.IsVisible = false;
            _cellDetailEnabled = false;
            UpdateCellDetailToggleButton();
        };
        CellDetailToggleButton.Click += (_, _) =>
        {
            _cellDetailEnabled = !_cellDetailEnabled;
            UpdateCellDetailToggleButton();
            if (_cellDetailEnabled)
                UpdateCellDetail();
            else
                CellDetailPanel.IsVisible = false;
        };

        // Drag-to-resize cell detail panel
        CellDetailResizeHandle.PointerPressed += OnCellDetailResizePressed;
        CellDetailResizeHandle.PointerMoved += OnCellDetailResizeMoved;
        CellDetailResizeHandle.PointerReleased += OnCellDetailResizeReleased;

        // Column header right-click for freeze/unfreeze
        ResultsGrid.AddHandler(PointerReleasedEvent, OnColumnHeaderPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Prevent column header double-click from triggering cell edit mode
        ResultsGrid.AddHandler(Avalonia.Input.Gestures.DoubleTappedEvent, OnColumnHeaderDoubleTapped, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Wire results collapse button + double-click results tab bar to toggle
        ResultsCollapseButton.Click += (_, _) => ToggleResultsPanel();
        ResultsTabBar.DoubleTapped += (_, _) => ToggleResultsPanel();

        // Double-click splitter to toggle between auto-sized and maximized (50/50)
        ResultsSplitter.DoubleTapped += (_, _) => ToggleResultsMaximized();

        // Peek Definition: Cmd+Click (Mac) / Ctrl+Click (Windows) on word in editor
        SqlEditor.AddHandler(InputElement.PointerPressedEvent, OnEditorPointerPressed, handledEventsToo: true);

        // Editor right-click context menu
        SqlEditor.AddHandler(InputElement.PointerReleasedEvent, OnEditorPointerReleased, handledEventsToo: true);
        PeekCloseButton.Click += (_, _) => ClosePeekPanel();

        // Apply syntax highlighting to peek editor too
        if (SqlEditor.SyntaxHighlighting != null)
            PeekEditor.SyntaxHighlighting = SqlEditor.SyntaxHighlighting;

        // Start with results panel collapsed — editor gets full height
        EditorResultsGrid.RowDefinitions[2].Height = new GridLength(0, GridUnitType.Pixel);
        ResultsSplitter.IsEnabled = false;
        ResultsCollapseButton.Content = "\u25B2"; // ▲
    }

    /// <summary>
    /// Insert text into the editor (replace all), optionally auto-run.
    /// </summary>
    public void InsertText(string sql, bool autoRun)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SqlEditor.Text = sql;
            if (_viewModel != null)
            {
                _viewModel.SqlText = sql;
                _viewModel.SelectedSqlText = "";
            }

            if (autoRun && _viewModel?.RunQueryCommand.CanExecute(null) == true)
                _ = _viewModel.RunQueryCommand.ExecuteAsync(null);
        });
    }

    /// <summary>
    /// Insert text at the current cursor position.
    /// </summary>
    public void InsertAtCursor(string text)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var offset = SqlEditor.CaretOffset;
            SqlEditor.Document.Insert(offset, text);
            SqlEditor.CaretOffset = offset + text.Length;
            SqlEditor.Focus();
        });
    }

    /// <summary>
    /// Handle F5 / Ctrl+Enter for query execution.
    /// </summary>
    /// <summary>Fired when user presses Ctrl+L — host generates exec plan.</summary>
    public event Action? ExecPlanRequested;

    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_viewModel == null) return false;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Space)
        {
            e.Handled = true;
            ShowCompletionWindow();
            return true;
        }

        // Ctrl+L = Estimated Execution Plan (matching SSMS)
        if (ctrl && !shift && e.Key == Key.L)
        {
            ExecPlanRequested?.Invoke();
            e.Handled = true;
            return true;
        }

        // Ctrl+Shift+F5 = Run with Trace
        if (e.Key == Key.F5 && ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.SelectedSqlText = SqlEditor.SelectedText ?? "";
            _viewModel.SelectionStartLine = !string.IsNullOrEmpty(_viewModel.SelectedSqlText)
                ? SqlEditor.Document.GetLineByOffset(SqlEditor.SelectionStart).LineNumber : 1;
            _viewModel.SqlText = SqlEditor.Text;

            if (!string.IsNullOrEmpty(_viewModel.SelectedSqlText))
                FlashExecutedSelection();

            if (_viewModel.RunWithTraceCommand.CanExecute(null))
                _ = _viewModel.RunWithTraceCommand.ExecuteAsync(null);

            return true;
        }

        if (e.Key == Key.F5 || (ctrl && e.Key == Key.Enter))
        {
            _viewModel.SelectedSqlText = SqlEditor.SelectedText ?? "";
            _viewModel.SelectionStartLine = !string.IsNullOrEmpty(_viewModel.SelectedSqlText)
                ? SqlEditor.Document.GetLineByOffset(SqlEditor.SelectionStart).LineNumber : 1;
            _viewModel.SqlText = SqlEditor.Text;

            if (!string.IsNullOrEmpty(_viewModel.SelectedSqlText))
                FlashExecutedSelection();

            if (_viewModel.RunQueryCommand.CanExecute(null))
                _ = _viewModel.RunQueryCommand.ExecuteAsync(null);

            return true;
        }

        // Alt+Up/Down (move lines), Ctrl+G (go to line)
        if (HandleEditorKeyDown(e))
            return true;

        return false;
    }

    public void RefreshTheme()
    {
        LoadSyntaxHighlighting();
        ApplyEditorSelectionColors();

        // Clear cached null brush so GetNullForeground() re-reads from resources
        _nullForeground = null!;

        // Update grid row height and editor font size from settings
        ApplyGridRowHeight();
        ApplyEditorFontSize();

        // Force DataGrid to re-render rows (re-fires LoadingRow with new theme colors)
        var source = ResultsGrid.ItemsSource;
        if (source != null)
        {
            ResultsGrid.ItemsSource = null;
            ResultsGrid.ItemsSource = source;
        }

        // Refresh result tab headers and highlight with new theme colors
        RebuildResultTabs();
        UpdateTabHighlight(_selectedTabIndex);
    }
}
