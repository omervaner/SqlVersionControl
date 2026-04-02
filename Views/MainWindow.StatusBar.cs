using Avalonia.Controls;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class MainWindow
{
    private QueryTabViewModel? _boundQueryTab;
    private Avalonia.Threading.DispatcherTimer? _queryStatusTimer;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsConnected) or nameof(MainWindowViewModel.ConnectionDisplay)
            or nameof(MainWindowViewModel.ConnectionColor))
        {
            UpdateStatusBar();
        }
    }

    private void UpdateStatusBar()
    {
        var isQE = QueryEditorTab.IsChecked == true;
        var isHistory = VersionHistoryTab.IsChecked == true;
        var isCompare = CompareTab.IsChecked == true;
        var isActivity = ActivityTab.IsChecked == true;
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeTabVm = host?.ActiveTabViewModel;

        // Each view owns its connection — status bar mirrors the active view
        if (_viewModel.IsConnected)
        {
            string displayColor;
            string displayText;

            if (isQE && activeTabVm?.TabConnectionProfile != null)
            {
                displayColor = activeTabVm.TabConnectionColor;
                displayText = activeTabVm.TabConnectionDisplay;
            }
            else if (isHistory)
            {
                displayColor = _viewModel.HistoryConnectionColor;
                displayText = _viewModel.HistoryConnectionDisplay;
            }
            else if (isActivity)
            {
                displayColor = _viewModel.ActivityConnectionColor;
                displayText = _viewModel.ActivityConnectionDisplay;
            }
            else
            {
                displayColor = _viewModel.ConnectionColor;
                displayText = _viewModel.ConnectionDisplay;
            }

            var color = Avalonia.Media.Color.Parse(displayColor);

            // Save last known connection info for offline state
            _lastConnectionColor = displayColor;
            _lastConnectionDisplay = displayText;

            if (_isOffline)
            {
                // Desaturated: grey dot, dimmed stripe at 20% opacity, "(offline)" suffix
                var grey = Avalonia.Media.Color.FromRgb(128, 128, 128);
                ConnectionDot.Fill = new Avalonia.Media.SolidColorBrush(grey);
                ConnectionText.Text = $"{displayText} (offline)";
                ReconnectNowButton.IsVisible = true;

                var dimColor = Avalonia.Media.Color.FromArgb(50, color.R, color.G, color.B);
                var dimTransparent = Avalonia.Media.Color.FromArgb(0, color.R, color.G, color.B);
                var gradientBrush = new Avalonia.Media.LinearGradientBrush
                {
                    StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                    EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
                    GradientStops =
                    {
                        new Avalonia.Media.GradientStop(dimTransparent, 0.0),
                        new Avalonia.Media.GradientStop(dimColor, 0.15),
                        new Avalonia.Media.GradientStop(dimColor, 0.85),
                        new Avalonia.Media.GradientStop(dimTransparent, 1.0),
                    }
                };
                ConnectionStripe.Background = gradientBrush;
                ConnectionStripe.IsVisible = true;
                this.Title = $"Lookout — {displayText} (offline)";
            }
            else
            {
                var solidBrush = new Avalonia.Media.SolidColorBrush(color);
                ConnectionDot.Fill = solidBrush;
                ConnectionText.Text = displayText;
                ReconnectNowButton.IsVisible = false;

                // Gradient fade at both horizontal ends
                var transparent = Avalonia.Media.Color.FromArgb(0, color.R, color.G, color.B);
                var gradientBrush = new Avalonia.Media.LinearGradientBrush
                {
                    StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                    EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
                    GradientStops =
                    {
                        new Avalonia.Media.GradientStop(transparent, 0.0),
                        new Avalonia.Media.GradientStop(color, 0.15),
                        new Avalonia.Media.GradientStop(color, 0.85),
                        new Avalonia.Media.GradientStop(transparent, 1.0),
                    }
                };
                ConnectionStripe.Background = gradientBrush;
                ConnectionStripe.IsVisible = true;
                var activeDb = this.FindControl<QueryEditorHost>("QueryEditorHostControl")
                    ?.ActiveTabViewModel?.SelectedDatabase;
                this.Title = !string.IsNullOrEmpty(activeDb)
                    ? $"Lookout — {displayText} / {activeDb}"
                    : $"Lookout — {displayText}";
            }
        }
        else
        {
            if (Avalonia.Application.Current?.Resources.TryGetResource("DisconnectedDot", null, out var dotBrush) == true && dotBrush is Avalonia.Media.IBrush disconnectedBrush)
                ConnectionDot.Fill = disconnectedBrush;
            else
                ConnectionDot.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(231, 76, 60));
            ConnectionText.Text = "Disconnected";
            ConnectionStripe.IsVisible = false;
            this.Title = "Lookout";
        }

        // Quick-switch buttons
        RebuildQuickSwitchButtons();

        // Query status section — only visible on Query Editor tab
        QueryStatusSeparator.IsVisible = isQE;
        QueryStatusText.IsVisible = isQE;
        CursorPositionText.IsVisible = isQE;
        if (!isQE) QueryFlashText.IsVisible = false;

        if (isQE)
            BindActiveQueryTab();
        else
            UnbindQueryTab();

        // Keep crash context up to date
        CrashLogger.ActiveConnection = _lastConnectionDisplay;
        CrashLogger.ActiveDatabase = activeTabVm?.SelectedDatabase;
        CrashLogger.ActiveTabName = activeTabVm?.TabTitle;
    }

    private void BindActiveQueryTab()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeVm = host?.ActiveTabViewModel;

        if (activeVm == _boundQueryTab) return;

        UnbindQueryTab();

        if (activeVm == null) return;
        _boundQueryTab = activeVm;
        _boundQueryTab.PropertyChanged += OnQueryTabPropertyChanged;
        _boundQueryTab.QueryFlash += OnQueryFlash;
        QueryStatusText.Text = _boundQueryTab.QueryStatusText;
    }

    private void UnbindQueryTab()
    {
        if (_boundQueryTab != null)
        {
            _boundQueryTab.PropertyChanged -= OnQueryTabPropertyChanged;
            _boundQueryTab.QueryFlash -= OnQueryFlash;
            _boundQueryTab = null;
        }
        _queryStatusTimer?.Stop();
        _queryStatusTimer = null;
        QueryStatusText.Text = "";
        QueryFlashText.Text = "";
        QueryFlashText.IsVisible = false;
    }

    private void OnQueryTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_boundQueryTab == null) return;

        if (e.PropertyName == nameof(QueryTabViewModel.QueryStatusText))
            QueryStatusText.Text = _boundQueryTab.QueryStatusText;
        else if (e.PropertyName == nameof(QueryTabViewModel.SelectedDatabase))
            UpdateStatusBar();
    }

    private void OnQueryFlash(string message, QueryStatusSeverity severity)
    {
        QueryFlashText.Text = message;
        QueryFlashText.IsVisible = true;
        QueryFlashText.Foreground = severity switch
        {
            QueryStatusSeverity.Success => GetBrush("ButtonPrimary"),
            QueryStatusSeverity.Warning => GetBrush("WarningSeverityWarning"),
            QueryStatusSeverity.Error => GetBrush("ButtonDanger"),
            _ => GetBrush("TextSecondary"),
        };

        _queryStatusTimer?.Stop();
        _queryStatusTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _queryStatusTimer.Tick += (_, _) =>
        {
            _queryStatusTimer?.Stop();
            _queryStatusTimer = null;
            QueryFlashText.Foreground = GetBrush("TextSecondary");
        };
        _queryStatusTimer.Start();
    }

    private static Avalonia.Media.IBrush GetBrush(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var r) == true
        && r is Avalonia.Media.IBrush b ? b : Avalonia.Media.Brushes.Gray;
}
