using Avalonia.Controls;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class TraceView : UserControl
{
    private TraceViewModel? _viewModel;

    public TraceView()
    {
        InitializeComponent();
    }

    public void Initialize(ConnectionRegistry registry)
    {
        _viewModel = new TraceViewModel();
        _viewModel.SetConnections(registry);
        DataContext = _viewModel;

        // Subscribe to state changes to toggle panel visibility
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TraceViewModel.State))
                UpdatePanelVisibility();
        };

        UpdatePanelVisibility();
    }

    private void UpdatePanelVisibility()
    {
        if (_viewModel == null) return;

        SetupPanel.IsVisible = _viewModel.State == TraceState.Setup;
        RecordingPanel.IsVisible = _viewModel.State == TraceState.Recording;
        ResultsPanel.IsVisible = _viewModel.State == TraceState.Results;
    }

    /// <summary>
    /// Refresh connection list (e.g., after new connections are established).
    /// </summary>
    public void RefreshConnections(ConnectionRegistry registry)
    {
        _viewModel?.SetConnections(registry);
    }
}
