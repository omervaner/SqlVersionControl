using Avalonia.Controls;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class TraceView : UserControl
{
    private TraceViewModel? _viewModel;
    private ConnectionRegistry? _registry;

    public TraceView()
    {
        InitializeComponent();
    }

    public ConnectionIndicator ConnectionIndicator => TraceConnectionIndicator;

    public void Initialize(ConnectionRegistry registry)
    {
        _registry = registry;
        _viewModel = new TraceViewModel();
        _viewModel.SetConnections(registry);
        DataContext = _viewModel;

        // Wire connection indicator
        TraceConnectionIndicator.Initialize(registry);
        TraceConnectionIndicator.ConnectionSelected += OnConnectionSelected;
        TraceConnectionIndicator.NewConnectionRequested += OnNewConnectionRequested;

        // Auto-select first active connection in indicator
        var first = registry.ActiveConnections.FirstOrDefault();
        if (first != null)
            TraceConnectionIndicator.SetActiveConnection(first);

        // Subscribe to state changes to toggle panel visibility
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TraceViewModel.State))
                UpdatePanelVisibility();
        };

        UpdatePanelVisibility();
    }

    private async void OnConnectionSelected(ManagedConnection managed)
    {
        if (_registry == null || _viewModel == null) return;

        if (!managed.IsConnected)
            await _registry.ConnectAsync(managed.Id);

        if (managed.IsConnected && managed.ResolvedConnectionString != null)
        {
            _viewModel.SetConnectionDirect(managed.ResolvedConnectionString);
            TraceConnectionIndicator.SetActiveConnection(managed);
        }
    }

    /// <summary>Fired when the user clicks "+ New Connection..." — handled by MainWindow.</summary>
    public event Action? NewConnectionRequested;

    private void OnNewConnectionRequested()
    {
        NewConnectionRequested?.Invoke();
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
