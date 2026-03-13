using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiffPlex.DiffBuilder.Model;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class DiffView : UserControl
{
    public static readonly StyledProperty<SideBySideDiffModel?> DiffModelProperty =
        AvaloniaProperty.Register<DiffView, SideBySideDiffModel?>(nameof(DiffModel));

    public SideBySideDiffModel? DiffModel
    {
        get => GetValue(DiffModelProperty);
        set => SetValue(DiffModelProperty, value);
    }

    private bool _syncingScroll;
    private SideBySideDiffModel? _lastModel;

    private static IBrush FindBrush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b
            ? b : Brushes.Transparent;

    public DiffView()
    {
        InitializeComponent();
        ApplyTheme();

        PropertyChanged += (s, e) =>
        {
            if (e.Property == DiffModelProperty)
                OnDiffModelChanged(DiffModel);
        };

        // Sync scrolling
        LeftScroll.ScrollChanged += (s, e) => SyncScroll(LeftScroll, RightScroll);
        RightScroll.ScrollChanged += (s, e) => SyncScroll(RightScroll, LeftScroll);
    }

    public void ApplyTheme()
    {
        var bg = FindBrush("EditorBackground");
        var splitterBg = FindBrush("SplitterBackground");

        LeftPanel.Background = bg;
        RightPanel.Background = bg;
        DiffSplitter.Background = splitterBg;

        // Re-render diff lines to update colors
        if (_lastModel != null)
        {
            OnDiffModelChanged(_lastModel);
        }
    }

    private void SyncScroll(ScrollViewer source, ScrollViewer target)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        target.Offset = source.Offset;
        _syncingScroll = false;
    }

    private void OnDiffModelChanged(SideBySideDiffModel? model)
    {
        _lastModel = model;

        if (model == null)
        {
            LeftLines.ItemsSource = null;
            RightLines.ItemsSource = null;
            return;
        }

        LeftLines.ItemsSource = model.OldText.Lines.Select((line, i) => new DiffLine
        {
            LineNumber = line.Position?.ToString() ?? "",
            Text = line.Text ?? "",
            Type = line.Type
        }).ToList();

        RightLines.ItemsSource = model.NewText.Lines.Select((line, i) => new DiffLine
        {
            LineNumber = line.Position?.ToString() ?? "",
            Text = line.Text ?? "",
            Type = line.Type
        }).ToList();
    }
}
