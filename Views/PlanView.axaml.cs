using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class PlanView : UserControl
{
    private PlanViewModel? _viewModel;
    private readonly List<(int NodeId, Border Border)> _costBarBorders = new();

    private static IBrush GetHighlightBrush()
    {
        if (Application.Current?.Resources.TryGetResource("SelectionActive", null, out var brush) == true && brush is IBrush b)
            return b;
        return new SolidColorBrush(Color.FromArgb(80, 74, 158, 255));
    }

    public PlanView()
    {
        InitializeComponent();
    }

    public void Initialize(DatabaseService db, MainWindowViewModel mainVm)
    {
        _viewModel = new PlanViewModel(db, mainVm);
        DataContext = _viewModel;
        _viewModel.PlanChanged += RebuildCostBar;
        _viewModel.HighlightChanged += OnHighlightChanged;

        SqlTextBlock.SelectionBrush = GetHighlightBrush();

        ThemeManager.ThemeChanged += () => SqlTextBlock.SelectionBrush = GetHighlightBrush();
    }

    public PlanViewModel? ViewModel => _viewModel;

    /// <summary>
    /// Initializes as an embedded component (no MainWindowViewModel needed).
    /// Hides the top toolbar since plan is loaded externally.
    /// </summary>
    public void InitializeEmbedded(DatabaseService db)
    {
        _viewModel = new PlanViewModel(db);
        DataContext = _viewModel;
        _viewModel.PlanChanged += RebuildCostBar;
        _viewModel.HighlightChanged += OnHighlightChanged;

        SqlTextBlock.SelectionBrush = GetHighlightBrush();
        ThemeManager.ThemeChanged += () => SqlTextBlock.SelectionBrush = GetHighlightBrush();

        // Hide the top toolbar (Generate Plan button is not needed in embedded mode)
        if (this.Content is Avalonia.Controls.Grid grid && grid.Children.Count > 0
            && grid.Children[0] is Avalonia.Controls.Border toolbar)
        {
            toolbar.IsVisible = false;
        }
    }

    /// <summary>
    /// Loads a pre-generated plan for display (embedded use).
    /// </summary>
    public void LoadPlan(string sql, string planXml, string label)
    {
        _viewModel?.LoadFromQuery(sql, planXml, label);
    }

    private void OnHighlightChanged()
    {
        if (_viewModel == null) return;

        // Always clear previous selection first
        SqlTextBlock.SelectionEnd = 0;
        SqlTextBlock.SelectionStart = 0;

        var start = _viewModel.HighlightStart;
        var end = _viewModel.HighlightEnd;

        if (start >= 0 && end > start)
        {
            SqlTextBlock.SelectionStart = start;
            SqlTextBlock.SelectionEnd = end;
            ScrollToHighlight(start);
        }

        UpdateCostBarSelection();
    }

    /// <summary>
    /// Approximate scroll to bring the highlighted statement into view.
    /// Monospace font at size 12 ≈ 16px line height.
    /// </summary>
    private void ScrollToHighlight(int charOffset)
    {
        var text = _viewModel?.SqlText;
        if (string.IsNullOrEmpty(text) || charOffset <= 0) return;

        var lineNumber = 0;
        for (int i = 0; i < charOffset && i < text.Length; i++)
            if (text[i] == '\n') lineNumber++;

        const double lineHeight = 16.0;
        var targetY = lineNumber * lineHeight;

        // Center the highlight in the viewport
        var viewportHeight = SqlScrollViewer.Viewport.Height;
        var scrollY = Math.Max(0, targetY - viewportHeight / 3);
        SqlScrollViewer.Offset = new Vector(SqlScrollViewer.Offset.X, scrollY);
    }

    /// <summary>
    /// Adds a bright border around the cost bar segment matching the selected node.
    /// </summary>
    private void UpdateCostBarSelection()
    {
        var selectedNodeId = _viewModel?.SelectedNode?.Node.NodeId ?? -999;

        foreach (var (nodeId, border) in _costBarBorders)
        {
            border.BorderBrush = nodeId == selectedNodeId
                ? Brushes.White
                : null;
            border.BorderThickness = nodeId == selectedNodeId
                ? new Thickness(2)
                : new Thickness(0);
        }
    }

    /// <summary>
    /// Rebuilds the cost bar grid with proportionally-sized colored segments.
    /// </summary>
    private void RebuildCostBar()
    {
        CostBarGrid.ColumnDefinitions.Clear();
        CostBarGrid.Children.Clear();
        _costBarBorders.Clear();

        if (_viewModel == null) return;

        var segments = _viewModel.CostSegments;
        if (segments.Count == 0) return;

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // Star-sized column proportional to cost percentage
            CostBarGrid.ColumnDefinitions.Add(
                new ColumnDefinition(seg.Percentage, GridUnitType.Star));

            var border = new Border
            {
                Background = SolidColorBrush.Parse(seg.Color),
                Margin = new Avalonia.Thickness(1, 0),
                CornerRadius = new Avalonia.CornerRadius(4),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                ClipToBounds = true
            };

            // Label inside the segment — show for ≥ 15%, tooltip-only for smaller
            if (seg.Percentage >= 15)
            {
                border.Child = new TextBlock
                {
                    Text = seg.Label,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Avalonia.Thickness(6, 0)
                };
            }

            ToolTip.SetTip(border, seg.RawLabel);

            // Click to select corresponding operator node
            var nodeId = seg.NodeId;
            border.PointerPressed += (s, e) =>
            {
                if (_viewModel == null) return;
                var match = _viewModel.OperatorNodes
                    .FirstOrDefault(n => n.Node.NodeId == nodeId);
                if (match != null)
                    _viewModel.SelectedNode = match;
            };

            Grid.SetColumn(border, i);
            CostBarGrid.Children.Add(border);
            _costBarBorders.Add((seg.NodeId, border));
        }
    }
}
