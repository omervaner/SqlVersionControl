using Avalonia;
using Avalonia.Controls;
using DiffPlex.DiffBuilder.Model;
using SqlVersionControl.Models;

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

    public DiffView()
    {
        InitializeComponent();

        PropertyChanged += (s, e) =>
        {
            if (e.Property == DiffModelProperty)
                OnDiffModelChanged(DiffModel);
        };

        // Sync scrolling
        LeftScroll.ScrollChanged += (s, e) => SyncScroll(LeftScroll, RightScroll);
        RightScroll.ScrollChanged += (s, e) => SyncScroll(RightScroll, LeftScroll);
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
