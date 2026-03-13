using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
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

    public void Initialize(QueryTabViewModel vm)
    {
        _viewModel = vm;
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

        // Show SQL preview button
        ShowSqlButton.Click += OnShowSqlClicked;

        // DataGrid row events for edit mode
        ResultsGrid.LoadingRow += OnDataGridLoadingRow;
        ResultsGrid.RowEditEnded += OnDataGridRowEditEnded;

        // Double-click result grid to auto-enter edit mode
        ResultsGrid.DoubleTapped += OnResultsGridDoubleTapped;
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
    }

    // ── Edit Mode ─────────────────────────────────────────────────────

    private void OnEditModeChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel == null) return;

            var resultIndex = _selectedTabIndex >= 0 && _selectedTabIndex < _viewModel.Results.Count
                ? _selectedTabIndex : 0;

            if (_viewModel.IsEditMode && _viewModel.EditableRows != null &&
                resultIndex < _viewModel.Results.Count)
            {
                // Enter edit mode: toggle columns writable + swap to EditableRows
                SetColumnsReadOnly(false);
                ResultsGrid.IsReadOnly = false;
                ResultsGrid.CanUserSortColumns = false;
                ResultsGrid.ItemsSource = _viewModel.EditableRows;
                SetupEditContextMenu();
            }
            else
            {
                // Exit edit mode: toggle columns read-only + swap back to raw rows
                SetColumnsReadOnly(true);
                ResultsGrid.IsReadOnly = true;
                ResultsGrid.CanUserSortColumns = true;
                ResultsGrid.ContextMenu = null;

                if (_viewModel.Results.Count > 0 && resultIndex < _viewModel.Results.Count)
                {
                    var result = _viewModel.Results[resultIndex];
                    if (result.Error == null)
                        ResultsGrid.ItemsSource = result.Rows;
                }
            }

            UpdateEditModeButton();
            UpdateEditBar();
        });
    }

    private void SetColumnsReadOnly(bool readOnly)
    {
        foreach (var col in ResultsGrid.Columns)
        {
            if (col is DataGridTextColumn textCol)
                textCol.IsReadOnly = readOnly;
        }
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

        EditBar.IsVisible = _viewModel.IsEditMode;
        if (_viewModel.IsEditMode)
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
        UpdateTabHighlight(index);
    }

    private static readonly NullDisplayConverter _nullTextConverter = new();
    private static readonly IBrush _nullForeground = new SolidColorBrush(Color.FromRgb(102, 102, 102));

    /// <summary>
    /// Single source of truth for building result grid columns.
    /// Columns use TwoWay + NullDisplayConverter so they work for both read-only and edit mode.
    /// </summary>
    private void BuildColumns(QueryResult result)
    {
        ResultsGrid.Columns.Clear();
        ResultsGrid.AutoGenerateColumns = false;

        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnNames[i],
                Binding = new Binding($"[{i}]", BindingMode.TwoWay) { Converter = _nullTextConverter },
                IsReadOnly = true,
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
