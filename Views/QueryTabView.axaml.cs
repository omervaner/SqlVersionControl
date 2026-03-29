using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

        ThemeManager.ThemeChanged += RefreshTheme;

        // Enable drag-and-drop on editor
        DragDrop.SetAllowDrop(SqlEditor, true);
        SqlEditor.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        SqlEditor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);

        vm.Results.CollectionChanged += OnResultsChanged;

        // Edit mode state changes
        vm.EditModeChanged += OnEditModeChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueryTabViewModel.PendingChangeCount))
                UpdateEditBar();
            if (e.PropertyName == nameof(QueryTabViewModel.IsEditMode))
                UpdateEditModeButton();
        };

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

        // Wire results collapse button + double-click results tab bar to toggle
        ResultsCollapseButton.Click += (_, _) => ToggleResultsPanel();
        ResultsTabBar.DoubleTapped += (_, _) => ToggleResultsPanel();

        // Peek Definition: Cmd+Click (Mac) / Ctrl+Click (Windows) on word in editor
        SqlEditor.AddHandler(InputElement.PointerPressedEvent, OnEditorPointerPressed, handledEventsToo: true);
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

        if (e.Key == Key.F5 || (ctrl && e.Key == Key.Enter))
        {
            _viewModel.SelectedSqlText = SqlEditor.SelectedText ?? "";
            _viewModel.SqlText = SqlEditor.Text;

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

    private void ApplyEditorFontSize()
    {
        var size = _settings?.Settings.FontSize ?? 12;
        SqlEditor.FontSize = size;
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

    private void ConfigureEditor()
    {
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;

        var defaultText = "-- Write your SQL query here\n-- Press F5 or Ctrl+Enter to execute\n\n";
        SqlEditor.Text = defaultText;
        _viewModel?.SetInitialText(defaultText);

        SqlEditor.TextChanged += (_, _) =>
        {
            if (_viewModel != null)
                _viewModel.SqlText = SqlEditor.Text;
        };

        SqlEditor.TextArea.TextEntering += OnTextEntering;
        SqlEditor.TextArea.TextEntered += OnTextEntered;

        // Section 11: Highlight all occurrences of selected word
        _occurrenceHighlighter = new OccurrenceHighlighter();
        SqlEditor.TextArea.TextView.LineTransformers.Add(_occurrenceHighlighter);
        SqlEditor.TextArea.SelectionChanged += (_, _) => UpdateOccurrenceHighlight();
        SqlEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateOccurrenceHighlight();
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

    private static Color GetWordHighlightColor()
    {
        if (Application.Current?.Resources.TryGetResource("WordHighlight", null, out var res) == true
            && res is SolidColorBrush brush)
            return brush.Color;
        return Color.FromRgb(61, 53, 32); // fallback dark amber
    }

    // ── Section 12: Move Line Up/Down ────────────────────────────────

    /// <summary>Handle Alt+Up/Down to move lines, Cmd/Ctrl+G for Go to Line.</summary>
    public bool HandleEditorKeyDown(KeyEventArgs e)
    {
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

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

        // Section 13: Go to Line
        if (ctrl && e.Key == Key.G)
        {
            ShowGoToLinePopup();
            e.Handled = true;
            return true;
        }

        return false;
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

    private void ShowGoToLinePopup()
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
        if (_viewModel is not { IsEditMode: true }) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelChangesCommand.Execute(null);
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);

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

    private async void OnExportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
            ? _selectedTabIndex : 0;
        if (resultIndex >= _viewModel.Results.Count) return;

        var result = _viewModel.Results[resultIndex];
        if (result.Error != null) return;

        // Determine which rows to export
        var selectedRows = GetSelectedRows();
        var rowsToExport = selectedRows.Count > 0 ? selectedRows : result.Rows;
        var isPartial = selectedRows.Count > 0;

        var path = await ShowExcelSaveDialog();
        if (path == null) return;

        try
        {
            ExportService.ExportToExcel(result.ColumnNames, result.ColumnTypes, rowsToExport, path);
            _viewModel.StatusText = isPartial
                ? $"Exported {rowsToExport.Count:N0} of {result.RowCount:N0} rows to {Path.GetFileName(path)}"
                : $"Exported {result.RowCount:N0} rows to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Export failed: {ex.Message}";
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

    private void SetupReadOnlyContextMenu()
    {
        var menu = new ContextMenu();

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

        var copyInsert = new MenuItem { Header = "Copy as INSERT" };
        copyInsert.Click += async (_, _) => await CopyAsInsertAsync();

        var copyRows = new MenuItem { Header = "Copy Selected Rows" };
        copyRows.Click += async (_, _) => await CopySelectedRowsAsync();

        menu.Items.Add(exportSelected);
        menu.Items.Add(copyInsert);
        menu.Items.Add(copyRows);

        menu.Opening += (_, _) =>
        {
            var hasSelection = ResultsGrid.SelectedItems.Count > 0;
            var hasTable = _viewModel?.EditTableSchema != null && _viewModel?.EditTableName != null;

            exportSelected.IsVisible = hasSelection;
            copyInsert.IsVisible = hasSelection && hasTable;
            copyRows.IsVisible = hasSelection;
        };

        ResultsGrid.ContextMenu = menu;
    }

    private async Task CopyAsInsertAsync()
    {
        try
        {
            if (_viewModel == null) return;
            var rows = GetSelectedRows();
            if (rows.Count == 0 || _viewModel.EditTableSchema == null || _viewModel.EditTableName == null) return;

            var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
                ? _selectedTabIndex : 0;
            var result = _viewModel.Results[resultIndex];

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
            Console.WriteLine($"CopyAsInsert crash: {ex}");
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

            var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
                ? _selectedTabIndex : 0;
            var result = _viewModel.Results[resultIndex];

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
            Console.WriteLine($"CopySelectedRows crash: {ex}");
            if (_viewModel != null)
                _viewModel.StatusText = $"Copy rows failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── Drag-and-Drop ─────────────────────────────────────────────────

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
        if (e.Data.Contains("ObjectExplorerNode"))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragEventArgs.Data is obsolete
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

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(SqlEditor);
        if (!point.Properties.IsLeftButtonPressed) return;

        // Check for Cmd (Mac) or Ctrl (Windows/Linux)
        var mods = e.KeyModifiers;
        var hasModifier = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
        if (!hasModifier) return;

        var word = GetWordAtCaret();
        if (string.IsNullOrWhiteSpace(word)) return;

        // Strip square brackets if present: [dbo].[MyProc] → MyProc
        word = word.Trim('[', ']');
        if (word.Length == 0) return;

        _ = PeekDefinitionAsync(word);
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
            EditorResultsGrid.RowDefinitions[0].Height = new GridLength(6, GridUnitType.Star);
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(4, GridUnitType.Star);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC"; // ▼
        }
    }

    private void ClosePeekPanel()
    {
        PeekPanel.IsVisible = false;
        // Restore previous result view
        if (_viewModel?.Results.Count > 0)
        {
            if (_selectedTabIndex >= 0)
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

    // ── Result Tabs ──────────────────────────────────────────────────

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildResultTabs();

        // Auto-expand results panel when new results arrive (70% editor / 30% results)
        try
        {
            if (_resultsCollapsed && _viewModel?.Results.Count > 0)
            {
                _resultsCollapsed = false;
                // Use saved height, or calculate 30% of available space
                var h = _settings?.Settings.ResultsPanelHeight ?? 0;
                if (h <= 0 || double.IsNaN(h) || double.IsInfinity(h))
                {
                    var totalHeight = EditorResultsGrid.Bounds.Height;
                    h = totalHeight > 100 ? totalHeight * 0.3 : 200;
                }
                EditorResultsGrid.RowDefinitions[0].Height = new GridLength(7, GridUnitType.Star);
                EditorResultsGrid.RowDefinitions[2].Height = new GridLength(3, GridUnitType.Star);
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
            Console.WriteLine($"Auto-expand results failed: {ex.Message}");
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
            Console.WriteLine($"ToggleResultsPanel failed: {ex.Message}");
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
            Console.WriteLine($"RestoreResultsPanelState failed: {ex.Message}");
        }
    }

    private void RebuildResultTabs()
    {
        if (_viewModel == null) return;

        ResultTabHeaders.Children.Clear();
        _selectedTabIndex = -1;

        var results = _viewModel.Results;

        if (results.Count == 0)
        {
            ResultsGrid.IsVisible = false;
            MessagesPanel.IsVisible = false;
            EmptyState.IsVisible = true;
            return;
        }

        EmptyState.IsVisible = false;

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var label = r.Error != null
                ? "Error"
                : $"Result {i + 1} ({r.RowCount} rows)";

            var idx = i;
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
                Tag = idx
            };
            btn.Click += (_, _) => SelectResultTab(idx);
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
            Tag = -1
        };
        msgBtn.Click += (_, _) => SelectMessagesTab();
        ResultTabHeaders.Children.Add(msgBtn);

        var firstGood = results.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Error == null);
        if (firstGood.r != null)
            SelectResultTab(firstGood.i);
        else
            SelectMessagesTab();
    }

    private void SelectResultTab(int index)
    {
        if (_viewModel == null || index < 0 || index >= _viewModel.Results.Count) return;

        // Exit edit mode if switching result tabs
        if (_viewModel.IsEditMode)
        {
            _viewModel.CancelChangesCommand.Execute(null);
        }

        _selectedTabIndex = index;
        MessagesPanel.IsVisible = false;
        EmptyState.IsVisible = false;

        var result = _viewModel.Results[index];

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

    private void SelectMessagesTab()
    {
        _selectedTabIndex = -1;
        ResultsGrid.IsVisible = false;
        MessagesPanel.IsVisible = true;
        EmptyState.IsVisible = false;
        UpdateTabHighlight(-1);
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
                var isMessages = (int)(btn.Tag ?? -1) == -1;
                var isSelected = isMessages
                    ? selectedIndex == -1
                    : (int)(btn.Tag ?? -1) == selectedIndex;

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
