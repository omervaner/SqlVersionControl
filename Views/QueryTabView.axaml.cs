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
