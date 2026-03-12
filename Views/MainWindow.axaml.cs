using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly SleepDetector _sleepDetector;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new SettingsService();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        // Apply saved theme and font size
        ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize);

        // Restore window position/size
        RestoreWindowPosition();

        // Subscribe to rollback confirmation requests
        _viewModel.RollbackRequested += OnRollbackRequested;

        // Initialize and wire up CompareView
        var compareView = this.FindControl<CompareView>("CompareViewControl");
        if (compareView != null)
        {
            compareView.Initialize(_settings);
            compareView.ViewModel.DeployRequested += OnDeployRequested;
            // Apply theme to Compare tab diff views after initialization
            compareView.RefreshTheme();
        }

        // Initialize PlanView with shared services
        var planView = this.FindControl<PlanView>("PlanViewControl");
        planView?.Initialize(_viewModel.DatabaseService, _viewModel);

        // Wire up dependencies button
        DependenciesButton.Click += async (s, e) => await ShowDependenciesAsync();

        // Wire up settings button
        SettingsButton.Click += async (s, e) => await ShowSettingsDialogAsync();

        // Wire up change DB button
        ChangeDbButton.Click += async (s, e) => await ChangeConnectionAsync();

        // Wire up retry button on reconnect overlay
        RetryButton.Click += async (s, e) => await ReconnectAsync();

        // Sleep/wake detection
        _sleepDetector = new SleepDetector();
        _sleepDetector.WokeFromSleep += OnWokeFromSleep;

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.D1:
                VersionHistoryTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D2:
                CompareTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D3:
                PlanTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.F:
                if (CompareTab.IsChecked == true)
                {
                    var compareView = this.FindControl<CompareView>("CompareViewControl");
                    compareView?.FocusSearch();
                }
                else
                {
                    VersionHistorySearchBox.Focus();
                    VersionHistorySearchBox.SelectAll();
                }
                e.Handled = true;
                break;

            case Key.R:
                if (_viewModel.IsConnected)
                    _ = _viewModel.RefreshCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.S:
                if (_viewModel.IsConnected)
                    _ = _viewModel.SyncCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.D:
                if (_viewModel.IsConnected)
                    _ = ShowDependenciesAsync();
                e.Handled = true;
                break;

            case Key.Escape when !ctrl:
                if (_viewModel.IsDependencyMode)
                {
                    _viewModel.BackFromDependenciesCommand.Execute(null);
                }
                else if (!string.IsNullOrEmpty(_viewModel.SearchText))
                {
                    _viewModel.SearchText = "";
                }
                else
                {
                    _viewModel.SelectedObject = null;
                    _viewModel.SelectedChange = null;
                }
                e.Handled = true;
                break;
        }
    }

    private void RestoreWindowPosition()
    {
        var s = _settings.Settings;
        if (s.WindowWidth.HasValue && s.WindowHeight.HasValue)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowX.HasValue && s.WindowY.HasValue)
        {
            Position = new Avalonia.PixelPoint((int)s.WindowX.Value, (int)s.WindowY.Value);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (s.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _sleepDetector.Stop();

        // Save window position/size
        var s = _settings.Settings;
        s.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        _settings.Save();
    }

    private async Task<bool> OnRollbackRequested(ObjectVersion version)
    {
        var dialog = new RollbackDialog(version);
        await dialog.ShowDialog(this);
        return dialog.Confirmed;
    }

    private async Task<bool> OnDeployRequested(string objectName, string targetDescription)
    {
        var isProd = targetDescription == "PRODUCTION";
        var dialog = new DeployDialog(objectName, targetDescription, isProd);
        await dialog.ShowDialog(this);
        return dialog.Confirmed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await ShowConnectionDialogAsync();
    }

    private async Task ShowConnectionDialogAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            _viewModel.OnConnected(dialog.Result);
            _sleepDetector.Start();
        }
        else
        {
            Close();
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new SettingsDialog(_settings, RefreshDiffViews);
        await dialog.ShowDialog(this);
    }

    private void RefreshDiffViews()
    {
        MainDiffView.ApplyTheme();
        var compareView = this.FindControl<CompareView>("CompareViewControl");
        compareView?.RefreshTheme();
    }

    private async Task ShowDependenciesAsync()
    {
        var obj = _viewModel.SelectedObject;
        if (obj == null || obj.IsSectionHeader || string.IsNullOrEmpty(_viewModel.SelectedDatabase)) return;

        await _viewModel.ShowDependenciesAsync(obj);
    }

    private async Task ChangeConnectionAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            // User connected to a new database - refresh the view
            _viewModel.OnConnected(dialog.Result);
            _sleepDetector.Start();
        }
        // If user cancels, just keep current connection (don't close app)
    }

    private async void OnWokeFromSleep()
    {
        if (!_viewModel.IsConnected) return;
        await ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        // Show overlay
        ReconnectOverlay.IsVisible = true;
        ReconnectText.Text = "Reconnecting...";
        ReconnectProgress.IsVisible = true;
        RetryButton.IsVisible = false;

        // Clear stale pooled connections
        SqlConnection.ClearAllPools();

        // Retry up to 3 times with 2s delay
        for (int i = 1; i <= 3; i++)
        {
            ReconnectText.Text = $"Reconnecting... (attempt {i}/3)";

            if (await _viewModel.DatabaseService.TestConnectionAsync())
            {
                // Success — hide overlay and refresh
                ReconnectOverlay.IsVisible = false;
                _viewModel.StatusMessage = "Reconnected after sleep";
                return;
            }

            if (i < 3)
                await Task.Delay(2000);
        }

        // All retries failed — show manual retry
        ReconnectText.Text = "Connection lost";
        ReconnectProgress.IsVisible = false;
        RetryButton.IsVisible = true;
    }
}
