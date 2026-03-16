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
    private bool _resultsCollapsed;

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
        DatabaseCombo.IsDropDownOpen = true;
        DatabaseCombo.Focus();
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

        // Wire results collapse button
        ResultsCollapseButton.Click += (_, _) => ToggleResultsPanel();
        RestoreResultsPanelState();
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

        return false;
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
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.Keyword);
                    break;
                case "String":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.String);
                    break;
                case "Comment":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.Comment);
                    break;
                case "Number":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.Number);
                    break;
                case "Variable":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.Variable);
                    break;
                case "SystemFunction":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.SystemFunction);
                    break;
                case "Identifier":
                    color.Foreground = new AvaloniaEdit.Highlighting.SimpleHighlightingBrush(ThemeManager.Dark.Identifier);
                    break;
            }
        }
    }

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
                    tb.Foreground = _nullForeground;
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

    private async Task CopySelectedRowsAsync()
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

    // ── Result Tabs ──────────────────────────────────────────────────

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildResultTabs();

        // Auto-expand results panel when new results arrive
        if (_resultsCollapsed && _viewModel?.Results.Count > 0)
        {
            _resultsCollapsed = false;
            var h = _settings?.Settings.ResultsPanelHeight ?? 200;
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(h, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC"; // ▼
            if (_settings != null)
            {
                _settings.Settings.ResultsPanelCollapsed = false;
                _settings.Save();
            }
        }
    }

    // ── Results Panel Collapse ──────────────────────────────────────

    public void ToggleResultsPanel()
    {
        var rowDefs = EditorResultsGrid.RowDefinitions;
        if (_resultsCollapsed)
        {
            // Expand — restore saved height
            var h = _settings?.Settings.ResultsPanelHeight ?? 200;
            rowDefs[2].Height = new GridLength(h, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = true;
            ResultsCollapseButton.Content = "\u25BC"; // ▼
            _resultsCollapsed = false;
        }
        else
        {
            // Save current height before collapsing
            var currentHeight = rowDefs[2].ActualHeight;
            if (currentHeight > 30 && _settings != null)
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

    private void RestoreResultsPanelState()
    {
        if (_settings == null) return;
        var s = _settings.Settings;

        if (s.ResultsPanelCollapsed)
        {
            EditorResultsGrid.RowDefinitions[2].Height = new GridLength(0, GridUnitType.Pixel);
            ResultsSplitter.IsEnabled = false;
            ResultsCollapseButton.Content = "\u25B2"; // ▲
            _resultsCollapsed = true;
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
                Padding = new Thickness(10, 5),
                Margin = new Thickness(2, 3),
                Foreground = GetRowBrush("TextBright"),
                Background = GetRowBrush("ButtonSecondary"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = idx
            };
            btn.Click += (_, _) => SelectResultTab(idx);
            ResultTabHeaders.Children.Add(btn);
        }

        // Messages tab
        var msgBtn = new Button
        {
            Content = "Messages",
            Padding = new Thickness(10, 5),
            Margin = new Thickness(2, 3),
            Foreground = GetRowBrush("ButtonForeground"),
            Background = GetRowBrush("ButtonSecondary"),
            Cursor = new Cursor(StandardCursorType.Hand),
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
    private static readonly IBrush _nullForeground = new SolidColorBrush(Color.FromRgb(102, 102, 102));

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
        var activeBrush = GetRowBrush("AccentBlue");
        var normalBrush = GetRowBrush("ButtonSecondary");

        for (int i = 0; i < ResultTabHeaders.Children.Count; i++)
        {
            if (ResultTabHeaders.Children[i] is Button btn)
            {
                var isMessages = (int)(btn.Tag ?? -1) == -1;
                var isSelected = isMessages
                    ? selectedIndex == -1
                    : (int)(btn.Tag ?? -1) == selectedIndex;

                btn.Background = isSelected ? activeBrush : normalBrush;
            }
        }
    }
}
