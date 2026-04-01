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

        // DataGrid row events for edit mode
        ResultsGrid.LoadingRow += OnDataGridLoadingRow;
        ResultsGrid.RowEditEnded += OnDataGridRowEditEnded;

        // Double-click result grid to auto-enter edit mode
        ResultsGrid.DoubleTapped += OnResultsGridDoubleTapped;

        // Keyboard shortcuts on results grid (Ctrl+V paste in edit mode)
        ResultsGrid.KeyDown += OnResultsGridKeyDown;

        // Cell detail viewer — track both row changes and cell clicks
        ResultsGrid.SelectionChanged += OnResultsGridCellSelected;
        ResultsGrid.CellPointerPressed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateCellDetail(), Avalonia.Threading.DispatcherPriority.Background);
        CellDetailCopyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(CellDetailText.Text ?? "");
        };
        CellDetailCloseButton.Click += (_, _) => CellDetailPanel.IsVisible = false;

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
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_viewModel == null) return false;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (ctrl && e.Key == Key.Space)
        {
            e.Handled = true;
            ShowCompletionWindow();
            return true;
        }

        // Ctrl+Shift+F5 = Run with Trace
        if (e.Key == Key.F5 && ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewModel.SelectedSqlText = SqlEditor.SelectedText ?? "";
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

    private void ApplyGridRowHeight()
    {
        var height = _settings?.Settings.GridRowHeight ?? 22;
        ResultsGrid.RowHeight = height;
    }

    private void UpdateEmptyStateText()
    {
        if (_viewModel == null) return;

        if (string.IsNullOrEmpty(_viewModel.TabConnectionString))
            EmptyState.Text = "Not connected — use File → Manage Connections to connect";
        else if (string.IsNullOrEmpty(_viewModel.SelectedDatabase))
            EmptyState.Text = "Select a database from the dropdown above";
        else
            EmptyState.Text = "Run a query to see results here";
    }

    private void ApplyEditorFontSize()
    {
        var size = _settings?.Settings.FontSize ?? 12;
        SqlEditor.FontSize = size;
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;

        var delta = e.Delta.Y > 0 ? 1 : -1;
        var currentSize = _settings?.Settings.FontSize ?? 12;
        var newSize = Math.Clamp(currentSize + delta, 8, 32);

        if (newSize == currentSize) return;

        if (_settings != null)
        {
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
        }

        e.Handled = true;
    }

    private void ApplyEditorSelectionColors()
    {
        if (Application.Current?.Resources.TryGetResource("EditorSelectionBrush", null, out var selBrush) == true
            && selBrush is Avalonia.Media.IBrush brush)
            SqlEditor.TextArea.SelectionBrush = brush;
        if (Application.Current?.Resources.TryGetResource("EditorSelectionForeground", null, out var selFg) == true
            && selFg is Avalonia.Media.IBrush fgBrush)
            SqlEditor.TextArea.SelectionForeground = fgBrush;
    }

    private void LoadSyntaxHighlighting()
    {
        IHighlightingDefinition? definition = null;

        try
        {
            var uri = new Uri("avares://SqlVersionControl/Assets/SQL.xshd");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            using var reader = new System.Xml.XmlTextReader(stream);
            definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            // Fall through to built-in
        }

        definition ??= HighlightingManager.Instance.GetDefinition("TSQL");

        // Apply ThemeManager colors to the highlighting definition
        if (definition != null)
        {
            ApplyThemeColors(definition);
            SqlEditor.SyntaxHighlighting = definition;
        }

        // Set editor background/foreground from theme
        if (Application.Current?.Resources.TryGetResource("EditorBackground", null, out var edBg) == true && edBg is IBrush edBrush)
            SqlEditor.Background = edBrush;
        if (Application.Current?.Resources.TryGetResource("TextPrimary", null, out var edFg) == true && edFg is IBrush fgBrush)
            SqlEditor.Foreground = fgBrush;
    }

    private static void ApplyThemeColors(IHighlightingDefinition definition)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            switch (color.Name)
            {
                case "Keyword":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetKeywordColor());
                    break;
                case "String":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetStringColor());
                    break;
                case "Comment":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetCommentColor());
                    break;
                case "Number":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetNumberColor());
                    break;
                case "Variable":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetVariableColor());
                    break;
                case "SystemFunction":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetSystemFunctionColor());
                    break;
                case "Identifier":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.GetIdentifierColor());
                    break;
            }
        }
    }

    private OccurrenceHighlighter? _occurrenceHighlighter;
    private BracketHighlighter? _bracketHighlighter;
    private AvaloniaEdit.Folding.FoldingManager? _foldingManager;
    private Timer? _foldingTimer;

    private void UpdatePlaceholder()
    {
        EditorPlaceholder.IsVisible = string.IsNullOrEmpty(SqlEditor.Text) && !SqlEditor.TextArea.IsFocused;
    }

    private void ConfigureEditor()
    {
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;

        SqlEditor.Text = "";
        _viewModel?.SetInitialText("");
        UpdatePlaceholder();

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_viewModel != null)
                _viewModel.SqlText = SqlEditor.Text;
            UpdatePlaceholder();
        };

        SqlEditor.TextArea.GotFocus += (_, _) => UpdatePlaceholder();
        SqlEditor.TextArea.LostFocus += (_, _) => UpdatePlaceholder();

        SqlEditor.TextArea.TextEntering += OnTextEntering;
        SqlEditor.TextArea.TextEntered += OnTextEntered;

        // Section 11: Highlight all occurrences of selected word
        _occurrenceHighlighter = new OccurrenceHighlighter();
        SqlEditor.TextArea.TextView.LineTransformers.Add(_occurrenceHighlighter);
        SqlEditor.TextArea.SelectionChanged += (_, _) => UpdateOccurrenceHighlight();
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateOccurrenceHighlight();

        // Bracket matching
        _bracketHighlighter = new BracketHighlighter();
        SqlEditor.TextArea.TextView.LineTransformers.Add(_bracketHighlighter);
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateBracketHighlight();

        // Code folding
        _foldingManager = AvaloniaEdit.Folding.FoldingManager.Install(SqlEditor.TextArea);
        var foldingStrategy = new SqlFoldingStrategy();
        SqlEditor.TextChanged += (_, _) =>
        {
            _foldingTimer?.Dispose();
            _foldingTimer = new Timer(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_foldingManager == null) return;
                    var foldings = foldingStrategy.CreateNewFoldings(SqlEditor.Document);
                    _foldingManager.UpdateFoldings(foldings, -1);
                });
            }, null, 500, Timeout.Infinite);
        };
    }

    // ── Section 11: Highlight All Occurrences ────────────────────────

    private void UpdateOccurrenceHighlight()
    {
        if (_occurrenceHighlighter == null) return;

        var selection = SqlEditor.SelectedText?.Trim();

        // Only highlight if it's a whole word (no spaces, not empty)
        if (string.IsNullOrWhiteSpace(selection) || selection.Contains(' ') || selection.Contains('\n'))
        {
            _occurrenceHighlighter.SelectedWord = null;
            SqlEditor.TextArea.TextView.Redraw();
            return;
        }

        // Check it's a "word" (alphanumeric/underscore)
        if (!selection.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@'))
        {
            _occurrenceHighlighter.SelectedWord = null;
            SqlEditor.TextArea.TextView.Redraw();
            return;
        }

        _occurrenceHighlighter.SelectedWord = selection;
        _occurrenceHighlighter.HighlightColor = GetWordHighlightColor();
        SqlEditor.TextArea.TextView.Redraw();
    }

    private void UpdateBracketHighlight()
    {
        if (_bracketHighlighter == null) return;

        var offset = SqlEditor.CaretOffset;
        var text = SqlEditor.Text;
        var match = FindMatchingBracket(text, offset);

        _bracketHighlighter.OpenOffset = match?.openOffset ?? -1;
        _bracketHighlighter.CloseOffset = match?.closeOffset ?? -1;
        SqlEditor.TextArea.TextView.Redraw();
    }

    private static (int openOffset, int closeOffset)? FindMatchingBracket(string text, int offset)
    {
        if (offset > 0)
        {
            var ch = text[offset - 1];
            if (ch == '(') return FindClosingBracket(text, offset - 1, '(', ')');
            if (ch == ')') return FindOpeningBracket(text, offset - 1, '(', ')');
        }
        if (offset < text.Length)
        {
            var ch = text[offset];
            if (ch == '(') return FindClosingBracket(text, offset, '(', ')');
            if (ch == ')') return FindOpeningBracket(text, offset, '(', ')');
        }
        return null;
    }

    private static (int openOffset, int closeOffset)? FindClosingBracket(string text, int openPos, char open, char close)
    {
        int depth = 1;
        for (int i = openPos + 1; i < text.Length && depth > 0; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) { depth--; if (depth == 0) return (openPos, i); }
        }
        return null;
    }

    private static (int openOffset, int closeOffset)? FindOpeningBracket(string text, int closePos, char open, char close)
    {
        int depth = 1;
        for (int i = closePos - 1; i >= 0 && depth > 0; i--)
        {
            if (text[i] == close) depth++;
            else if (text[i] == open) { depth--; if (depth == 0) return (i, closePos); }
        }
        return null;
    }

    private void OnEditorDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Check if the double-clicked word is BEGIN or END
        var doc = SqlEditor.Document;
        var offset = SqlEditor.CaretOffset;
        if (offset < 0 || offset > doc.TextLength) return;

        var text = doc.Text;

        // Find the word under cursor (AvaloniaEdit already selected it by now)
        int wordStart = offset, wordEnd = offset;
        while (wordStart > 0 && char.IsLetterOrDigit(text[wordStart - 1])) wordStart--;
        while (wordEnd < text.Length && char.IsLetterOrDigit(text[wordEnd])) wordEnd++;
        var word = text[wordStart..wordEnd];

        if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            // Skip BEGIN TRAN / BEGIN TRANSACTION
            var afterBegin = wordEnd;
            while (afterBegin < text.Length && char.IsWhiteSpace(text[afterBegin])) afterBegin++;
            if (IsKeywordAtPosition(text, afterBegin, "TRAN") || IsKeywordAtPosition(text, afterBegin, "TRANSACTION"))
                return;

            var match = FindMatchingEnd(text, wordStart);
            if (match >= 0)
            {
                var selStart = wordStart;
                var selLen = match + 3 - wordStart;
                // Defer to override AvaloniaEdit's built-in word selection
                Dispatcher.UIThread.Post(() => SqlEditor.Select(selStart, selLen),
                    DispatcherPriority.Background);
                e.Handled = true;
            }
        }
        else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            var match = FindMatchingBegin(text, wordStart);
            if (match >= 0)
            {
                var selStart = match;
                var selLen = wordEnd - match;
                Dispatcher.UIThread.Post(() => SqlEditor.Select(selStart, selLen),
                    DispatcherPriority.Background);
                e.Handled = true;
            }
        }
    }

    private static int FindMatchingEnd(string text, int beginPos)
    {
        int depth = 1;
        int i = beginPos + 5;
        while (i < text.Length && depth > 0)
        {
            if (text[i] == '\'') { i++; while (i < text.Length && text[i] != '\'') i++; i++; continue; }
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++; i += 2; continue; }

            if (IsKeywordAtPosition(text, i, "BEGIN"))
            {
                var after = i + 5;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (!IsKeywordAtPosition(text, after, "TRAN") && !IsKeywordAtPosition(text, after, "TRANSACTION"))
                    depth++;
                i += 5;
                continue;
            }
            if (IsKeywordAtPosition(text, i, "END"))
            {
                depth--;
                if (depth == 0) return i;
                i += 3;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int FindMatchingBegin(string text, int endPos)
    {
        // Scan backwards — simpler approach: collect all BEGIN/END positions forward, then match
        var pairs = new List<(int pos, bool isBegin)>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\'') { i++; while (i < text.Length && text[i] != '\'') i++; i++; continue; }
            if (i < text.Length - 1 && text[i] == '-' && text[i + 1] == '-')
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i < text.Length - 1 && text[i] == '/' && text[i + 1] == '*')
            { i += 2; while (i < text.Length - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++; i += 2; continue; }

            if (IsKeywordAtPosition(text, i, "BEGIN"))
            {
                var after = i + 5;
                while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
                if (!IsKeywordAtPosition(text, after, "TRAN") && !IsKeywordAtPosition(text, after, "TRANSACTION"))
                    pairs.Add((i, true));
                i += 5;
                continue;
            }
            if (IsKeywordAtPosition(text, i, "END"))
            {
                pairs.Add((i, false));
                i += 3;
                continue;
            }
            i++;
        }

        // Walk backwards from the target END to find its matching BEGIN
        int depth = 0;
        for (int j = pairs.Count - 1; j >= 0; j--)
        {
            if (pairs[j].pos == endPos) { depth = 1; continue; }
            if (depth == 0) continue;
            if (!pairs[j].isBegin) depth++;
            else { depth--; if (depth == 0) return pairs[j].pos; }
        }
        return -1;
    }

    private static bool IsKeywordAtPosition(string text, int pos, string keyword)
    {
        if (pos + keyword.Length > text.Length) return false;
        if (pos > 0 && char.IsLetterOrDigit(text[pos - 1])) return false;
        if (!text.AsSpan(pos, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        var after = pos + keyword.Length;
        return after >= text.Length || !char.IsLetterOrDigit(text[after]);
    }

    private static Color GetWordHighlightColor()
    {
        if (Application.Current?.Resources.TryGetResource("WordHighlight", null, out var res) == true
            && res is SolidColorBrush brush)
            return brush.Color;
        return Color.FromRgb(61, 53, 32); // fallback dark amber
    }

    // ── Executed Selection Flash ─────────────────────────────────────

    private ExecutionFlashHighlighter? _flashHighlighter;

    /// <summary>
    /// Briefly highlights the executed selection range (300ms) to confirm what was run.
    /// </summary>
    private void FlashExecutedSelection()
    {
        var selection = SqlEditor.TextArea.Selection;
        if (selection.IsEmpty) return;

        var startOffset = SqlEditor.SelectionStart;
        var endOffset = startOffset + SqlEditor.SelectionLength;
        if (startOffset >= endOffset) return;

        // Remove previous flash if still active
        if (_flashHighlighter != null)
        {
            SqlEditor.TextArea.TextView.LineTransformers.Remove(_flashHighlighter);
            _flashHighlighter = null;
        }

        var isDark = ThemeManager.IsDarkTheme;
        _flashHighlighter = new ExecutionFlashHighlighter
        {
            StartOffset = startOffset,
            EndOffset = endOffset,
            FlashColor = isDark
                ? Color.FromArgb(50, 80, 160, 255)   // subtle blue on dark
                : Color.FromArgb(50, 30, 100, 200)    // subtle blue on light
        };

        SqlEditor.TextArea.TextView.LineTransformers.Add(_flashHighlighter);
        SqlEditor.TextArea.TextView.Redraw();

        // Remove after 300ms
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_flashHighlighter != null)
            {
                SqlEditor.TextArea.TextView.LineTransformers.Remove(_flashHighlighter);
                _flashHighlighter = null;
                SqlEditor.TextArea.TextView.Redraw();
            }
        };
        timer.Start();
    }

    // ── Section 12: Move Line Up/Down ────────────────────────────────

    /// <summary>Handle Alt+Up/Down to move lines, Cmd/Ctrl+G for Go to Line.</summary>
    public bool HandleEditorKeyDown(KeyEventArgs e)
    {
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (alt && e.Key == Key.Up)
        {
            MoveLines(-1);
            e.Handled = true;
            return true;
        }
        if (alt && e.Key == Key.Down)
        {
            MoveLines(1);
            e.Handled = true;
            return true;
        }

        // Word Wrap Toggle: Alt+Z
        if (alt && e.Key == Key.Z)
        {
            SqlEditor.WordWrap = !SqlEditor.WordWrap;
            e.Handled = true;
            return true;
        }

        // Section 13: Go to Line
        if (ctrl && e.Key == Key.G)
        {
            ShowGoToLinePopup();
            e.Handled = true;
            return true;
        }

        // Comment: Cmd/Ctrl+K
        if (ctrl && !shift && e.Key == Key.K)
        {
            CommentLines();
            e.Handled = true;
            return true;
        }

        // Uncomment: Cmd/Ctrl+L
        if (ctrl && !shift && e.Key == Key.L)
        {
            UncommentLines();
            e.Handled = true;
            return true;
        }

        // Uppercase: Cmd/Ctrl+Shift+U
        if (ctrl && shift && e.Key == Key.U)
        {
            TransformSelection(s => s.ToUpperInvariant());
            e.Handled = true;
            return true;
        }

        // Lowercase: Cmd/Ctrl+Shift+L
        if (ctrl && shift && e.Key == Key.L)
        {
            TransformSelection(s => s.ToLowerInvariant());
            e.Handled = true;
            return true;
        }

        return false;
    }

    public void CommentLines()
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            if (sel.EndPosition.Column == 1 && endLine > startLine) endLine--;
        }

        doc.BeginUpdate();
        for (var line = startLine; line <= endLine; line++)
        {
            var docLine = doc.GetLineByNumber(line);
            doc.Insert(docLine.Offset, "-- ");
        }
        doc.EndUpdate();
    }

    public void UncommentLines()
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            if (sel.EndPosition.Column == 1 && endLine > startLine) endLine--;
        }

        doc.BeginUpdate();
        for (var line = startLine; line <= endLine; line++)
        {
            var docLine = doc.GetLineByNumber(line);
            var text = doc.GetText(docLine.Offset, docLine.Length);
            var trimmed = text.TrimStart();
            var indent = text.Length - trimmed.Length;
            if (trimmed.StartsWith("-- "))
                doc.Remove(docLine.Offset + indent, 3);
            else if (trimmed.StartsWith("--"))
                doc.Remove(docLine.Offset + indent, 2);
        }
        doc.EndUpdate();
    }

    public void UppercaseSelection() => TransformSelection(s => s.ToUpperInvariant());
    public void LowercaseSelection() => TransformSelection(s => s.ToLowerInvariant());

    private void TransformSelection(Func<string, string> transform)
    {
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;
        if (sel.IsEmpty) return;

        var doc = SqlEditor.Document;
        var start = sel.SurroundingSegment.Offset;
        var length = sel.SurroundingSegment.Length;
        var text = doc.GetText(start, length);
        var transformed = transform(text);

        doc.BeginUpdate();
        doc.Replace(start, length, transformed);
        doc.EndUpdate();

        // Restore selection
        textArea.Selection = AvaloniaEdit.Editing.Selection.Create(textArea, start, start + transformed.Length);
    }

    private void MoveLines(int direction)
    {
        var doc = SqlEditor.Document;
        var textArea = SqlEditor.TextArea;
        var sel = textArea.Selection;

        int startLine, endLine;
        if (sel.IsEmpty)
        {
            startLine = endLine = textArea.Caret.Line;
        }
        else
        {
            startLine = sel.StartPosition.Line;
            endLine = sel.EndPosition.Line;
            // If selection ends at column 1 of a line, don't include that line
            if (sel.EndPosition.Column == 1 && endLine > startLine)
                endLine--;
        }

        var targetLine = direction < 0 ? startLine - 1 : endLine + 1;
        if (targetLine < 1 || targetLine > doc.LineCount) return;

        // Get the block of lines to move
        var blockStart = doc.GetLineByNumber(startLine);
        var blockEnd = doc.GetLineByNumber(endLine);
        var blockOffset = blockStart.Offset;
        var blockLength = blockEnd.EndOffset - blockStart.Offset;
        var blockText = doc.GetText(blockOffset, blockLength);

        var swapDocLine = doc.GetLineByNumber(targetLine);
        var swapText = doc.GetText(swapDocLine.Offset, swapDocLine.Length);

        doc.BeginUpdate();
        try
        {
            if (direction < 0)
            {
                // Moving up: swap the line above with our block
                doc.Replace(blockStart.Offset, blockLength, swapText);
                doc.Replace(swapDocLine.Offset, swapDocLine.Length, blockText);
            }
            else
            {
                // Moving down: swap our block with the line below
                doc.Replace(swapDocLine.Offset, swapDocLine.Length, blockText);
                doc.Replace(blockStart.Offset, blockLength, swapText);
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        // Move caret to follow the moved block
        var newStartLine = startLine + direction;
        var newEndLine = endLine + direction;
        var newCaretLine = textArea.Caret.Line + direction;
        if (newCaretLine >= 1 && newCaretLine <= doc.LineCount)
        {
            textArea.Caret.Line = newCaretLine;
        }
    }

    // ── Section 13: Go to Line ───────────────────────────────────────

    private TextBox? _goToLineBox;

    public void ShowGoToLinePopup()
    {
        if (_goToLineBox != null)
        {
            // Already showing — focus it
            _goToLineBox.Focus();
            _goToLineBox.SelectAll();
            return;
        }

        // Create lightweight input overlay at top of editor

        var box = new TextBox
        {
            Watermark = $"Go to line (1–{SqlEditor.Document.LineCount})",
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(8, 4),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Width = 250,
            Margin = new Thickness(0, 4, 0, 0),
            ZIndex = 100
        };

        _goToLineBox = box;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (int.TryParse(box.Text, out var line) && line >= 1 && line <= SqlEditor.Document.LineCount)
                {
                    SqlEditor.TextArea.Caret.Line = line;
                    SqlEditor.TextArea.Caret.Column = 1;
                    SqlEditor.ScrollTo(line, 1);
                    SqlEditor.Focus();
                }
                CloseGoToLinePopup();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseGoToLinePopup();
                SqlEditor.Focus();
                e.Handled = true;
            }
        };

        box.LostFocus += (_, _) => CloseGoToLinePopup();

        // Add to the Grid at Row 0 (on top of editor)
        Grid.SetRow(box, 0);
        EditorResultsGrid.Children.Add(box);
        box.Focus();
    }

    private void CloseGoToLinePopup()
    {
        if (_goToLineBox != null)
        {
            EditorResultsGrid.Children.Remove(_goToLineBox);
            _goToLineBox = null;
        }
    }

    // ── Intellisense / Autocomplete ─────────────────────────────────

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow == null || string.IsNullOrEmpty(e.Text)) return;

        var ch = e.Text[0];
        if (!char.IsLetterOrDigit(ch) && ch != '_')
        {
            _completionWindow.CompletionList.RequestInsertion(e);
            _completionWindow = null; // Clear immediately, don't wait for Closed event
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_intellisenseService == null || string.IsNullOrEmpty(e.Text)) return;
        if (_isAutocompleteEnabled?.Invoke() == false) return;

        var ch = e.Text[0];
        if (!char.IsLetter(ch) && ch != '.') return;

        ShowCompletionWindow(ch == '.');
    }

    private void ShowCompletionWindow(bool isDot = false)
    {
        if (_intellisenseService == null) return;

        // Close any existing window and clear the reference
        if (_completionWindow != null)
        {
            _completionWindow.Close();
            _completionWindow = null;
        }

        var text = SqlEditor.Text;
        var offset = SqlEditor.CaretOffset;

        var completions = _intellisenseService.GetCompletions(text, offset);
        if (completions.Count == 0) return;

        _completionWindow = new CompletionWindow(SqlEditor.TextArea);

        if (!isDot)
        {
            var wordStart = offset;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                wordStart--;
            _completionWindow.StartOffset = wordStart;
        }

        _completionWindow.Closed += (_, _) =>
        {
            SqlEditor.TextChanged -= OnTextChangedWhileCompleting;
            _completionWindow = null;
        };

        foreach (var item in completions)
            _completionWindow.CompletionList.CompletionData.Add(item);

        _completionWindow.Show();
        SqlEditor.TextChanged += OnTextChangedWhileCompleting;
    }

    private void OnTextChangedWhileCompleting(object? sender, EventArgs e)
    {
        if (_completionWindow == null) return;

        if (SqlEditor.CaretOffset <= _completionWindow.StartOffset)
            _completionWindow.Close();
    }

    // ── Edit Mode ─────────────────────────────────────────────────────

    private void OnEditModeChanged()
    {
        if (_viewModel == null) return;
        CellDetailPanel.IsVisible = false;

        var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
            ? _selectedTabIndex : 0;

        if (_viewModel.IsEditMode && _viewModel.EditableRows != null &&
            resultIndex < _viewModel.Results.Count)
        {
            // Enter edit mode: rebuild columns without converter + swap to EditableRows
            var result = _viewModel.Results[resultIndex];
            BuildColumns(result, isEditMode: true);
            ResultsGrid.IsReadOnly = false;
            ResultsGrid.CanUserSortColumns = false;
            ResultsGrid.ItemsSource = _viewModel.EditableRows;
            SetupEditContextMenu();
        }
        else
        {
            // Exit edit mode: delegate to SelectResultTab (single source of truth)
            ResultsGrid.CanUserSortColumns = true;
            if (resultIndex >= 0 && resultIndex < (_viewModel.Results?.Count ?? 0))
                SelectResultTab(resultIndex);
        }

        UpdateEditModeButton();
        UpdateEditBar();
    }

    // ── Cell Detail Panel Resize ────────────────────────────────────
    private bool _cellDetailResizing;
    private Point _cellDetailResizeStart;
    private double _cellDetailStartHeight;

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

    private async void OnResultsGridDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not { CanEditMode: true, IsEditMode: false }) return;

        // Capture which row/column was double-clicked before edit mode rebuilds the grid
        var rowIndex = ResultsGrid.SelectedIndex;
        var colIndex = ResultsGrid.CurrentColumn is { } col
            ? ResultsGrid.Columns.IndexOf(col) : -1;

        // Enter edit mode (rebuilds columns + ItemsSource)
        await _viewModel.ToggleEditModeCommand.ExecuteAsync(null);

        // Wait a frame for the grid to rebuild, then select the cell and begin editing
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (rowIndex >= 0 && _viewModel?.EditableRows != null && rowIndex < _viewModel.EditableRows.Count)
                ResultsGrid.SelectedIndex = rowIndex;
            if (colIndex >= 0 && colIndex < ResultsGrid.Columns.Count)
                ResultsGrid.CurrentColumn = ResultsGrid.Columns[colIndex];
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

        if (_viewModel is not { IsEditMode: true }) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelChangesCommand.Execute(null);
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

    private IBrush AlternateBrush => GetRowBrush("ResultsAlternateRow");

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
    }

    private void StyleNullCells(DataGridRow row)
    {
        row.LayoutUpdated += OnRowLayoutForNulls;

        void OnRowLayoutForNulls(object? s, EventArgs args)
        {
            row.LayoutUpdated -= OnRowLayoutForNulls;

            // Get the underlying values array from either row type
            object?[]? values = row.DataContext switch
            {
                object?[] arr => arr,
                EditableRow er => er.Values,
                _ => null
            };
            if (values == null) return;

            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();
            for (int i = 0; i < values.Length && i < cells.Count; i++)
            {
                var tb = cells[i].FindDescendantOfType<TextBlock>();
                if (tb == null) continue;

                if (values[i] == null)
                {
                    tb.FontStyle = FontStyle.Italic;
                    tb.Foreground = GetNullForeground();
                }
                else
                {
                    tb.FontStyle = FontStyle.Normal;
                    tb.ClearValue(TextBlock.ForegroundProperty);
                }
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

        var dialog = new Window
        {
            Title = "Preview SQL — Changes will be executed in a single transaction",
            Width = 650,
            Height = 420,
            Content = new Border
            {
                Padding = new Thickness(10),
                Child = new ScrollViewer { Content = textBox }
            },
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent != null)
            await dialog.ShowDialog(parent);
    }

    private void OnExportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var menu = new MenuFlyout();

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

        menu.ShowAt(ExportButton, true);
    }

    private async Task ExportResultsAsync(string format)
    {
        if (_viewModel == null) return;

        var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
            ? _selectedTabIndex : 0;
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

        try
        {
            switch (format)
            {
                case "xlsx":
                    ExportService.ExportToExcel(result.ColumnNames, result.ColumnTypes, rowsToExport, path);
                    break;
                case "csv":
                    await File.WriteAllTextAsync(path, ResultToDelimited(result, rowsToExport, ","));
                    break;
                case "tsv":
                    await File.WriteAllTextAsync(path, ResultToDelimited(result, rowsToExport, "\t"));
                    break;
                case "json":
                    await File.WriteAllTextAsync(path, ResultToJson(result, rowsToExport));
                    break;
            }

            _viewModel.StatusText = isPartial
                ? $"Exported {rowsToExport.Count:N0} of {result.RowCount:N0} rows to {Path.GetFileName(path)}"
                : $"Exported {result.RowCount:N0} rows to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Export failed: {ex.Message}";
        }
    }

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
            _viewModel.StatusText = $"Copied {rows.Count} row{(rows.Count == 1 ? "" : "s")} with headers";
        }
    }

    private List<object?[]> GetSelectedRows()
    {
        var list = new List<object?[]>();
        foreach (var item in ResultsGrid.SelectedItems)
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

    /// <summary>Fired when "Filter by Value" is clicked — host should open a new tab.</summary>
    public event Action<string>? FilterByValueRequested;

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

    // ── Drag-and-Drop ─────────────────────────────────────────────────

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
        if (e.Data.Contains("ObjectExplorerNode") || e.Data.Contains(DataFormats.Files))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
#pragma warning restore CS0618
    }

    /// <summary>Fired when a .sql file is dropped on the editor (opens in new tab).</summary>
    public event Action<string>? FileDropped;

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
        // Handle external file drops (.sql files → open in new tab)
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null)
            {
                foreach (var item in files)
                {
                    if (item is Avalonia.Platform.Storage.IStorageFile file)
                    {
                        var path = file.Path.LocalPath;
                        if (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                            FileDropped?.Invoke(path);
                    }
                }
            }
            return;
        }

        if (!e.Data.Contains("ObjectExplorerNode")) return;
        var node = e.Data.Get("ObjectExplorerNode") as ObjectExplorerNode;
#pragma warning restore CS0618
        if (node == null) return;

        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Table:
                InsertAtDropPosition(e, $"SELECT TOP 100 * FROM [{schema}].[{node.Name}]");
                break;

            case ObjectExplorerNodeType.View:
                InsertAtDropPosition(e, $"[{schema}].[{node.Name}]");
                break;

            case ObjectExplorerNodeType.Function:
                InsertAtDropPosition(e, $"[{schema}].[{node.Name}]()");
                break;

            case ObjectExplorerNodeType.Column:
                InsertAtDropPosition(e, $"[{node.Name}]");
                break;

            case ObjectExplorerNodeType.Proc:
                HandleProcDrop(node);
                break;
        }
    }

    private void InsertAtDropPosition(DragEventArgs e, string text)
    {
        // Try to get the drop position in the editor
        var pos = e.GetPosition(SqlEditor);
        var textPos = SqlEditor.GetPositionFromPoint(pos);
        if (textPos != null)
        {
            var offset = SqlEditor.Document.GetOffset(textPos.Value.Line, textPos.Value.Column);
            SqlEditor.Document.Insert(offset, text);
            SqlEditor.CaretOffset = offset + text.Length;
        }
        else
        {
            // Fallback: insert at cursor
            var offset = SqlEditor.CaretOffset;
            SqlEditor.Document.Insert(offset, text);
            SqlEditor.CaretOffset = offset + text.Length;
        }
        SqlEditor.Focus();
    }

    private void HandleProcDrop(ObjectExplorerNode node)
    {
        ProcDropRequested?.Invoke(node);
    }

    /// <summary>Fired when a proc is dropped — host should fetch definition and route back.</summary>
    public event Action<ObjectExplorerNode>? ProcDropRequested;

    // ── Peek Definition (Cmd+Click / Ctrl+Click) ────────────────────

    /// <summary>Fired when user Cmd/Ctrl+Clicks a word — host fetches definition and calls ShowPeekDefinition.</summary>
    public event Func<string, Task<string?>>? PeekDefinitionRequested;

    /// <summary>Fired when user Shift+Clicks a word — host fetches params and opens exec template.</summary>
    public event Func<string, Task>? QuickExecuteRequested;

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(SqlEditor);
        if (!point.Properties.IsLeftButtonPressed) return;

        var mods = e.KeyModifiers;
        var hasCmdCtrl = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
        var hasShift = mods.HasFlag(KeyModifiers.Shift);

        if (!hasCmdCtrl && !hasShift) return;

        var word = GetWordAtCaret();
        if (string.IsNullOrWhiteSpace(word)) return;

        // Strip square brackets if present: [dbo].[MyProc] → MyProc
        word = word.Trim('[', ']');
        if (word.Length == 0) return;

        if (hasShift && !hasCmdCtrl)
        {
            // Shift+Click → Quick Execute
            _ = QuickExecuteRequested?.Invoke(word);
        }
        else if (hasCmdCtrl)
        {
            // Cmd/Ctrl+Click → Peek Definition
            _ = PeekDefinitionAsync(word);
        }
        e.Handled = true;
    }

    private string? GetWordAtCaret()
    {
        var doc = SqlEditor.Document;
        var offset = SqlEditor.CaretOffset;
        if (offset < 0 || offset > doc.TextLength) return null;

        // Expand left and right to find word boundaries (letters, digits, underscore, brackets, dot)
        int start = offset, end = offset;
        while (start > 0 && IsWordChar(doc.GetCharAt(start - 1))) start--;
        while (end < doc.TextLength && IsWordChar(doc.GetCharAt(end))) end++;

        if (start == end) return null;
        return doc.GetText(start, end - start);
    }

    private static bool IsWordChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '[' || c == ']' || c == '.' || c == '#';

    private async Task PeekDefinitionAsync(string objectName)
    {
        if (PeekDefinitionRequested == null) return;

        var definition = await PeekDefinitionRequested.Invoke(objectName);
        if (definition == null)
        {
            // Show "not found" briefly in the peek panel
            ShowPeekPanel($"Peek: {objectName}", $"-- Object '{objectName}' not found or is not a scriptable object");
            return;
        }

        ShowPeekPanel($"Peek: {objectName}", definition);
    }

    private void ShowPeekPanel(string title, string content)
    {
        PeekTitle.Text = title;
        PeekEditor.Text = content;

        // Apply syntax highlighting from main editor
        if (SqlEditor.SyntaxHighlighting != null)
            PeekEditor.SyntaxHighlighting = SqlEditor.SyntaxHighlighting;
        ApplyThemeToEditor(PeekEditor);

        // Show peek, hide other result panels
        ResultsGrid.IsVisible = false;
        MessagesPanel.IsVisible = false;
        EmptyState.IsVisible = false;
        PeekPanel.IsVisible = true;

        // Expand results panel if collapsed
        if (_resultsCollapsed)
        {
            _resultsCollapsed = false;
            var totalHeight = EditorResultsGrid.Bounds.Height;
            var peekHeight = totalHeight > 0 ? totalHeight * 0.4 : 250;
            EditorResultsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(peekHeight, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC"; // ▼
        }
    }

    private void ClosePeekPanel()
    {
        PeekPanel.IsVisible = false;
        // Restore previous result view
        if (_viewModel?.Results.Count > 0 || _pinnedResults.Count > 0)
        {
            if (_selectedTabIndex != MessagesTabTag && _selectedTabIndex != -1)
                SelectResultTab(_selectedTabIndex);
            else
                SelectMessagesTab();
        }
        else
        {
            EmptyState.IsVisible = true;
        }
    }

    private void ApplyThemeToEditor(TextEditor editor)
    {
        editor.Background = new SolidColorBrush(ThemeManager.GetDiffBackground());
        editor.Foreground = new SolidColorBrush(ThemeManager.GetIdentifierColor());
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        ShowEditorContextMenu(e);
        e.Handled = true;
    }

    // ── Editor Right-Click Context Menu ─────────────────────────────

    /// <summary>Fired when context menu requests Format SQL (lives on host).</summary>
    public event Action? FormatSqlRequested;

    /// <summary>Fired when context menu requests Quick Quote (lives on host).</summary>
    public event Action? QuickQuoteRequested;

    /// <summary>Fired when context menu requests Show Dependencies for a word.</summary>
    public event Action<string>? ShowDependenciesRequested;

    private void ShowEditorContextMenu(PointerReleasedEventArgs e)
    {
        var menu = new MenuFlyout();
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var mod = isMac ? "Cmd" : "Ctrl";

        // Clipboard
        menu.Items.Add(CreateEditorMenuItem("Cut", $"{mod}+X", () => SqlEditor.Cut()));
        menu.Items.Add(CreateEditorMenuItem("Copy", $"{mod}+C", () => SqlEditor.Copy()));
        menu.Items.Add(CreateEditorMenuItem("Paste", $"{mod}+V", () => SqlEditor.Paste()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateEditorMenuItem("Select All", $"{mod}+A", () => SqlEditor.SelectAll()));
        menu.Items.Add(new Separator());

        // Formatting
        menu.Items.Add(CreateEditorMenuItem("Format SQL", "Ctrl+Shift+F", () => FormatSqlRequested?.Invoke()));
        menu.Items.Add(CreateEditorMenuItem("Comment Lines", $"{mod}+K", CommentLines));
        menu.Items.Add(CreateEditorMenuItem("Uncomment Lines", $"{mod}+L", UncommentLines));
        menu.Items.Add(CreateEditorMenuItem("Uppercase", $"{mod}+Shift+U", () => TransformSelection(s => s.ToUpperInvariant())));
        menu.Items.Add(CreateEditorMenuItem("Lowercase", $"{mod}+Shift+L", () => TransformSelection(s => s.ToLowerInvariant())));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateEditorMenuItem("Quick Quote Selection", "Ctrl+Shift+Q", () => QuickQuoteRequested?.Invoke()));
        menu.Items.Add(new Separator());

        // Navigation
        menu.Items.Add(CreateEditorMenuItem("Go to Line...", $"{mod}+G", ShowGoToLinePopup));
        menu.Items.Add(CreateEditorMenuItem("Find", $"{mod}+F", () => AvaloniaEdit.Search.SearchPanel.Install(SqlEditor)));
        menu.Items.Add(CreateEditorMenuItem("Replace", $"{mod}+H", () => AvaloniaEdit.Search.SearchPanel.Install(SqlEditor)));

        // Contextual items — only if cursor is on a word
        var word = GetWordAtCaret();
        if (!string.IsNullOrEmpty(word))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateEditorMenuItem($"Peek Definition: {word}", $"{mod}+Click", () => _ = PeekDefinitionAsync(word)));
            menu.Items.Add(CreateEditorMenuItem($"Quick Execute: {word}", "Shift+Click", () => _ = QuickExecuteRequested?.Invoke(word)));
            menu.Items.Add(CreateEditorMenuItem($"Show Dependencies: {word}", "", () => ShowDependenciesRequested?.Invoke(word)));
        }

        menu.ShowAt(SqlEditor, true);
    }

    private static MenuItem CreateEditorMenuItem(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (!string.IsNullOrEmpty(gesture))
            item.InputGesture = null; // just display text, not bound
        item.Click += (_, _) => action();

        // Show shortcut hint as right-aligned text
        if (!string.IsNullOrEmpty(gesture))
            item.Header = $"{header}";

        return item;
    }

    // ── Result Tabs ──────────────────────────────────────────────────

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

    private bool _resultsMaximized;

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
        var hasMessages = !string.IsNullOrEmpty(_viewModel.Messages);
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
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var label = r.Error != null
                ? "Error"
                : $"Result {i + 1} ({r.RowCount} rows)";

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

        // Messages tab
        var msgBtn = new Button
        {
            Content = "Messages",
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

    /// <summary>Fired when user wants to open a source query in a new tab.</summary>
    public event Action<string>? OpenSourceQueryRequested;

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

    private static readonly NullDisplayConverter _nullTextConverter = new();
    private static IBrush? _nullForeground;

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

    /// <summary>
    /// Single source of truth for building result grid columns.
    /// Read-only mode: TwoWay + NullDisplayConverter (shows "NULL" for nulls).
    /// Edit mode: TwoWay, no converter (raw values, empty = null).
    /// </summary>
    private void BuildColumns(QueryResult result, bool isEditMode = false)
    {
        ResultsGrid.Columns.Clear();
        ResultsGrid.AutoGenerateColumns = false;
        ResultsGrid.FrozenColumnCount = 0;

        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            var binding = new Binding($"[{i}]", BindingMode.TwoWay);
            if (!isEditMode)
                binding.Converter = _nullTextConverter;

            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnNames[i],
                Binding = binding,
                IsReadOnly = !isEditMode,
            });
        }
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

    private const int MessagesTabTag = -1000;
    private const int TraceTabTag = -2000;

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

/// <summary>
/// AvaloniaEdit line transformer that highlights all occurrences of the selected word.
/// </summary>
internal class OccurrenceHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public string? SelectedWord { get; set; }
    public Color HighlightColor { get; set; } = Color.FromRgb(61, 53, 32);

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (string.IsNullOrEmpty(SelectedWord)) return;

        var lineText = CurrentContext.Document.GetText(line.Offset, line.Length);
        var wordLen = SelectedWord.Length;
        var idx = 0;

        while (idx <= lineText.Length - wordLen)
        {
            var pos = lineText.IndexOf(SelectedWord, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;

            // Whole-word check
            var before = pos > 0 ? lineText[pos - 1] : ' ';
            var after = pos + wordLen < lineText.Length ? lineText[pos + wordLen] : ' ';
            if (!IsWordBoundary(before) || !IsWordBoundary(after))
            {
                idx = pos + 1;
                continue;
            }

            var startOffset = line.Offset + pos;
            var endOffset = startOffset + wordLen;

            ChangeLinePart(startOffset, endOffset, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(HighlightColor));
            });

            idx = pos + wordLen;
        }
    }

    private static bool IsWordBoundary(char c)
        => !char.IsLetterOrDigit(c) && c != '_' && c != '#' && c != '@';
}

/// <summary>
/// Temporary line transformer that flashes a highlight on the executed selection range.
/// </summary>
internal class ExecutionFlashHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public Color FlashColor { get; set; } = Color.FromArgb(60, 100, 180, 255); // subtle blue

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (StartOffset >= EndOffset) return;

        // Check if this line overlaps with the flash range
        var lineStart = line.Offset;
        var lineEnd = line.Offset + line.Length;

        var overlapStart = Math.Max(lineStart, StartOffset);
        var overlapEnd = Math.Min(lineEnd, EndOffset);

        if (overlapStart < overlapEnd)
        {
            ChangeLinePart(overlapStart, overlapEnd, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(FlashColor));
            });
        }
    }
}

/// <summary>
/// Highlights matching bracket pairs (parentheses) when cursor is adjacent.
/// </summary>
internal class BracketHighlighter : AvaloniaEdit.Rendering.DocumentColorizingTransformer
{
    public int OpenOffset { get; set; } = -1;
    public int CloseOffset { get; set; } = -1;

    protected override void ColorizeLine(AvaloniaEdit.Document.DocumentLine line)
    {
        if (OpenOffset < 0 || CloseOffset < 0) return;

        var brush = GetBracketHighlightBrush();

        HighlightIfOnLine(line, OpenOffset, brush);
        HighlightIfOnLine(line, CloseOffset, brush);
    }

    private void HighlightIfOnLine(AvaloniaEdit.Document.DocumentLine line, int offset, SolidColorBrush brush)
    {
        if (offset >= line.Offset && offset < line.EndOffset)
        {
            ChangeLinePart(offset, offset + 1, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(brush);
            });
        }
    }

    private static SolidColorBrush GetBracketHighlightBrush()
    {
        if (Application.Current?.Resources.TryGetResource("BracketMatchBackground", null, out var res) == true
            && res is SolidColorBrush brush)
            return brush;
        return new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    }
}
