using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using SqlVersionControl.Models;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryTabView : UserControl
{
    private QueryTabViewModel? _viewModel;
    private int _selectedTabIndex = -1;

    // Row state colors
    private static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.Parse("#44ffff00")); // Yellow tint
    private static readonly IBrush NewBrush = new SolidColorBrush(Color.Parse("#4400cc00"));       // Green tint
    private static readonly IBrush DeletedBrush = new SolidColorBrush(Color.Parse("#44ff3333"));   // Red tint

    public QueryTabView()
    {
        InitializeComponent();
    }

    public TextEditor Editor => SqlEditor;

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
        try
        {
            var dir = AppContext.BaseDirectory;
            var xshdPath = Path.Combine(dir, "Assets", "SQL.xshd");

            if (File.Exists(xshdPath))
            {
                using var stream = File.OpenRead(xshdPath);
                using var reader = new System.Xml.XmlTextReader(stream);
                SqlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                return;
            }
        }
        catch
        {
            // Fall through to built-in
        }

        SqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
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
                var result = _viewModel.Results[resultIndex];

                // Rebuild columns with TwoWay binding so DataGrid can write back edits
                ResultsGrid.Columns.Clear();
                ResultsGrid.AutoGenerateColumns = false;
                for (int i = 0; i < result.ColumnNames.Length; i++)
                {
                    ResultsGrid.Columns.Add(new DataGridTextColumn
                    {
                        Header = result.ColumnNames[i],
                        Binding = new Avalonia.Data.Binding($"[{i}]",
                            Avalonia.Data.BindingMode.TwoWay),
                        IsReadOnly = false
                    });
                }

                ResultsGrid.IsReadOnly = false;
                ResultsGrid.CanUserSortColumns = false;
                ResultsGrid.ItemsSource = _viewModel.EditableRows;

                SetupEditContextMenu();
            }
            else
            {
                // Restore read-only mode — rebuild columns with default (OneWay) binding
                ResultsGrid.IsReadOnly = true;
                ResultsGrid.CanUserSortColumns = true;
                ResultsGrid.ContextMenu = null;

                if (_viewModel.Results.Count > 0 && resultIndex < _viewModel.Results.Count)
                {
                    var result = _viewModel.Results[resultIndex];
                    if (result.Error == null)
                    {
                        ResultsGrid.Columns.Clear();
                        ResultsGrid.AutoGenerateColumns = false;
                        for (int i = 0; i < result.ColumnNames.Length; i++)
                        {
                            ResultsGrid.Columns.Add(new DataGridTextColumn
                            {
                                Header = result.ColumnNames[i],
                                Binding = new Avalonia.Data.Binding($"[{i}]")
                            });
                        }
                        ResultsGrid.ItemsSource = result.Rows;
                    }
                }
            }

            UpdateEditModeButton();
            UpdateEditBar();
        });
    }

    private void UpdateEditModeButton()
    {
        if (_viewModel == null) return;

        if (_viewModel.IsEditMode)
        {
            EditModeButton.Content = "Editing";
            EditModeButton.Background = new SolidColorBrush(Color.Parse("#e67e22"));
        }
        else
        {
            EditModeButton.Content = "Edit";
            EditModeButton.Background = new SolidColorBrush(Color.Parse("#4a4a6e"));
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

    private void OnDataGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is EditableRow row)
            ApplyRowBackground(e.Row, row);
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
            _ => Brushes.Transparent
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
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.Parse("#4a4a6e")),
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
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#4a4a6e")),
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

        ResultsGrid.Columns.Clear();
        ResultsGrid.AutoGenerateColumns = false;

        for (int i = 0; i < result.ColumnNames.Length; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.ColumnNames[i],
                Binding = new Avalonia.Data.Binding($"[{i}]")
            });
        }

        ResultsGrid.ItemsSource = result.Rows;
        ResultsGrid.IsReadOnly = true;
        ResultsGrid.IsVisible = true;
        UpdateTabHighlight(index);
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
        var activeBrush = new SolidColorBrush(Color.Parse("#4a9eff"));
        var normalBrush = new SolidColorBrush(Color.Parse("#4a4a6e"));

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
