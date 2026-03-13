using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit.Search;
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
            compareView.RefreshTheme();
        }

        // Initialize PlanView with shared services
        var planView = this.FindControl<PlanView>("PlanViewControl");
        planView?.Initialize(_viewModel.DatabaseService, _viewModel);

        // Initialize QueryEditorHost with shared services
        var qeHost = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        qeHost?.Initialize(_viewModel.DatabaseService, _viewModel);

        // Wire up dependencies button
        DependenciesButton.Click += async (s, e) => await ShowDependenciesAsync();

        // Wire up settings button
        SettingsButton.Click += async (s, e) => await ShowSettingsDialogAsync();

        // Wire up change DB button
        ChangeDbButton.Click += async (s, e) => await ChangeConnectionAsync();

        // Wire up retry button on reconnect overlay
        RetryButton.Click += async (s, e) => await ReconnectAsync();

        // Wire menu items
        WireMenuItems();

        // Sleep/wake detection
        _sleepDetector = new SleepDetector();
        _sleepDetector.WokeFromSleep += OnWokeFromSleep;

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void WireMenuItems()
    {
        // File menu
        MenuNewQuery.Click += (_, _) => OnMenuNewQuery();
        MenuExit.Click += (_, _) => Close();

        // Edit menu — delegate to active tab's editor
        MenuUndo.Click += (_, _) => GetActiveEditor()?.Undo();
        MenuRedo.Click += (_, _) => GetActiveEditor()?.Redo();
        MenuCut.Click += (_, _) => GetActiveEditor()?.Cut();
        MenuCopy.Click += (_, _) => GetActiveEditor()?.Copy();
        MenuPaste.Click += (_, _) => GetActiveEditor()?.Paste();
        MenuFind.Click += (_, _) => OpenSearchPanel(false);
        MenuReplace.Click += (_, _) => OpenSearchPanel(true);

        // Query menu
        MenuRun.Click += (_, _) =>
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.RunActiveQuery();
        };
        MenuStop.Click += (_, _) =>
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.StopActiveQuery();
        };
        MenuChangeDb.Click += async (_, _) => await ChangeConnectionAsync();

        // Help menu
        MenuAbout.Click += async (_, _) => await ShowAboutDialogAsync();
        MenuCheckUpdates.Click += (_, _) => OpenUrl("https://github.com/omervaner/SqlVersionControl/releases");
    }

    private AvaloniaEdit.TextEditor? GetActiveEditor()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        return host?.GetActiveEditor();
    }

    private void OpenSearchPanel(bool withReplace)
    {
        var editor = GetActiveEditor();
        if (editor == null) return;

        var panel = SearchPanel.Install(editor);
        panel.Open();
        if (withReplace)
            panel.IsReplaceMode = true;
    }

    private void OnMenuNewQuery()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        host?.AddNewTab();

        // Switch to Query Editor tab if not already there
        QueryEditorTab.IsChecked = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // F5 — run query when Query Editor tab is active (no ctrl required)
        if (e.Key == Key.F5 && QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host?.HandleKeyDown(e) == true)
                e.Handled = true;
            return;
        }

        // Ctrl+Enter — run query when Query Editor tab is active
        if (ctrl && e.Key == Key.Enter && QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host?.HandleKeyDown(e) == true)
                e.Handled = true;
            return;
        }

        // Ctrl+N — new query tab
        if (ctrl && e.Key == Key.N)
        {
            OnMenuNewQuery();
            e.Handled = true;
            return;
        }

        // Ctrl+W — close active query tab
        if (ctrl && e.Key == Key.W && QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.CloseActiveTab();
            e.Handled = true;
            return;
        }

        if (!ctrl && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.D1:
                QueryEditorTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D2:
                VersionHistoryTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D3:
                CompareTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D4:
                PlanTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.F:
                if (CompareTab.IsChecked == true)
                {
                    var compareView = this.FindControl<CompareView>("CompareViewControl");
                    compareView?.FocusSearch();
                }
                else if (VersionHistoryTab.IsChecked == true)
                {
                    VersionHistorySearchBox.Focus();
                    VersionHistorySearchBox.SelectAll();
                }
                else if (QueryEditorTab.IsChecked == true)
                {
                    OpenSearchPanel(false);
                }
                e.Handled = true;
                break;

            case Key.H:
                if (QueryEditorTab.IsChecked == true)
                {
                    OpenSearchPanel(true);
                    e.Handled = true;
                }
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

            // Load databases into Query Editor Host
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host != null) _ = host.ReloadDatabasesAsync();
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

    private async Task ShowAboutDialogAsync()
    {
        var dialog = new AboutDialog();
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
            _viewModel.OnConnected(dialog.Result);
            _sleepDetector.Start();

            // Reload databases into Query Editor Host
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host != null) _ = host.ReloadDatabasesAsync();
        }
    }

    private async void OnWokeFromSleep()
    {
        if (!_viewModel.IsConnected) return;
        await ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        ReconnectOverlay.IsVisible = true;
        ReconnectText.Text = "Reconnecting...";
        ReconnectProgress.IsVisible = true;
        RetryButton.IsVisible = false;

        SqlConnection.ClearAllPools();

        for (int i = 1; i <= 3; i++)
        {
            ReconnectText.Text = $"Reconnecting... (attempt {i}/3)";

            if (await _viewModel.DatabaseService.TestConnectionAsync())
            {
                ReconnectOverlay.IsVisible = false;
                _viewModel.StatusMessage = "Reconnected after sleep";
                return;
            }

            if (i < 3)
                await Task.Delay(2000);
        }

        ReconnectText.Text = "Connection lost";
        ReconnectProgress.IsVisible = false;
        RetryButton.IsVisible = true;
    }

    private static void OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
}
