using Avalonia.Controls;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class MainWindow
{
    private async Task ShowConnectionDialogAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings, _registry);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            _viewModel.OnConnected(dialog.Result, dialog.ResultConnection);
            _sleepDetector.Start();

            // Set as default connection for new tabs
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.SetDefaultConnection(dialog.Result, dialog.ResultConnection);

            UpdateStatusBar();
            UpdateHistoryConnectionIndicator();

            // Load databases into Query Editor Host
            if (host != null) _ = host.ReloadDatabasesAsync();

            // Initialize Activity view with this connection
            UpdateActivityConnectionIndicator();
            var actView = this.FindControl<ActivityView>("ActivityViewControl");
            if (actView != null)
                _ = actView.InitializeConnectionAsync(dialog.Result.ConnectionString);

            // Cleanup orphaned trace sessions from previous crashes
            _ = Task.Run(async () =>
            {
                try
                {
                    var traceService = new TraceService();
                    await traceService.CleanupOrphanedSessionsAsync(dialog.Result.ConnectionString);
                }
                catch (Exception ex) { AppLogger.LogError("Trace.CleanupOrphaned", ex); }
            });

            // Refresh trace view connections
            var traceView = this.FindControl<TraceView>("TraceViewControl");
            traceView?.RefreshConnections(_registry);
        }
        else
        {
            // User chose "Continue Offline" or closed the dialog — let them use the app disconnected
            UpdateStatusBar();
        }
    }

    private async Task ChangeConnectionAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings, _registry);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            _viewModel.OnConnected(dialog.Result, dialog.ResultConnection);
            _sleepDetector.Start();

            // Set as default connection for new tabs
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.SetDefaultConnection(dialog.Result, dialog.ResultConnection);

            UpdateStatusBar();
            UpdateHistoryConnectionIndicator();

            // Reload databases into Query Editor Host
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
        DismissButton.IsVisible = true;

        SqlConnection.ClearAllPools();

        for (int i = 1; i <= 3; i++)
        {
            // If user dismissed the overlay, continue reconnecting in background
            if (!ReconnectOverlay.IsVisible)
            {
                await BackgroundReconnectAsync();
                return;
            }

            ReconnectText.Text = $"Reconnecting... (attempt {i}/3)";

            if (await _viewModel.DatabaseService.TestConnectionAsync())
            {
                OnReconnected();
                return;
            }

            if (i < 3)
                await Task.Delay(2000);
        }

        // All 3 foreground attempts failed — show retry, keep dismiss available
        if (ReconnectOverlay.IsVisible)
        {
            ReconnectText.Text = "Connection lost";
            ReconnectProgress.IsVisible = false;
            RetryButton.IsVisible = true;
        }
        else
        {
            // User dismissed during attempts — continue in background
            await BackgroundReconnectAsync();
        }
    }

    private void DismissReconnectOverlay()
    {
        ReconnectOverlay.IsVisible = false;
        _isOffline = true;
        UpdateStatusBar();
        _viewModel.StatusMessage = "Working offline — reconnecting in background";
    }

    private async Task BackgroundReconnectAsync()
    {
        _isOffline = true;
        UpdateStatusBar();

        // Retry every 10 seconds in the background until success
        while (_isOffline)
        {
            await Task.Delay(10000);
            if (!_isOffline) return; // Already reconnected or app closing

            try
            {
                if (await _viewModel.DatabaseService.TestConnectionAsync())
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(OnReconnected);
                    return;
                }
            }
            catch
            {
                // Keep retrying
            }
        }
    }

    private void OnReconnected()
    {
        _isOffline = false;
        ReconnectOverlay.IsVisible = false;
        _viewModel.StatusMessage = "Reconnected";
        UpdateStatusBar();

        // Clear per-server caches — tabs will re-validate on next query
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        host?.ClearServerCaches();
    }

    // ── Auto-Update ────────────────────────────────────────────────

    private async Task CheckForUpdatesAsync()
    {
        _updateService = new UpdateService();
        var hasUpdate = await _updateService.CheckForUpdateAsync();
        if (!hasUpdate) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateText.Text = $"Version {_updateService.AvailableVersion} is available";
            UpdateBar.IsVisible = true;
        });
    }

    private async void OnUpdateNowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updateService == null) return;

        UpdateNowButton.IsEnabled = false;
        UpdateNowButton.Content = "Downloading...";

        var success = await _updateService.DownloadUpdateAsync(progress =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                UpdateNowButton.Content = $"Downloading... {progress}%");
        });

        if (success)
        {
            UpdateNowButton.Content = "Restarting...";
            _updateService.ApplyUpdateAndRestart();
        }
        else
        {
            UpdateNowButton.Content = "Update Failed";
            UpdateNowButton.IsEnabled = true;
        }
    }

    // ── Crash Banner ────────────────────────────────────────────────

    private void OnCrashViewClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var report = CrashLogger.ReadLatestCrashReport();
        if (report == null) return;

        // Open crash report in a new query tab
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        host?.OpenScriptInNewTab($"-- CRASH REPORT\n-- Copy this and send to the dev team\n\n/*\n{report}\n*/");

        // Switch to editor tab
        QueryEditorTab.IsChecked = true;
    }

    private async void OnCrashCopyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var report = CrashLogger.ReadLatestCrashReport();
        if (report == null) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(report);

        CrashBannerText.Text = "Crash report copied to clipboard!";

        // Reset text after 2 seconds
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CrashBannerText.Text = "Lookout crashed last session.";
        };
        timer.Start();
    }

    // ── Quick-Switch Buttons ────────────────────────────────────────

    private void RebuildQuickSwitchButtons()
    {
        QuickSwitchPanel.Children.Clear();

        // Use registry connections (all named ones), fall back to settings
        var connections = _registry.Connections
            .Where(m => !string.IsNullOrEmpty(m.Config.Name))
            .Select(m => m.Config)
            .ToList();
        if (connections.Count == 0) return;

        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeProfile = host?.ActiveTabViewModel?.TabConnectionProfile;

        foreach (var conn in connections)
        {
            var isActive = activeProfile != null &&
                           activeProfile.Server == conn.Server &&
                           activeProfile.Database == conn.Database &&
                           activeProfile.Name == conn.Name;

            var color = Avalonia.Media.Color.Parse(conn.Color ?? "#88a1bb");
            var brush = new Avalonia.Media.SolidColorBrush(color);

            // On light theme, use darkened color for text/border to ensure contrast
            var displayBrush = brush;
            if (!ThemeManager.IsDarkTheme)
            {
                var darkened = Avalonia.Media.Color.FromRgb(
                    (byte)(color.R * 0.55),
                    (byte)(color.G * 0.55),
                    (byte)(color.B * 0.55));
                displayBrush = new Avalonia.Media.SolidColorBrush(darkened);
            }

            var btn = new Button
            {
                Content = conn.Name,
                FontSize = 10,
                Padding = new Avalonia.Thickness(6, 1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Background = isActive ? brush : Avalonia.Media.Brushes.Transparent,
                Foreground = isActive ? Avalonia.Media.Brushes.White : displayBrush,
                BorderBrush = displayBrush,
                BorderThickness = new Avalonia.Thickness(1),
                FontWeight = isActive ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                MinWidth = 0,
                MinHeight = 0,
            };

            var savedConn = conn;
            btn.Click += async (_, _) => await OnQuickSwitchClickedAsync(savedConn);
            QuickSwitchPanel.Children.Add(btn);
        }
    }

    private async Task OnQuickSwitchClickedAsync(SavedConnection conn)
    {
        _viewModel.StatusMessage = $"Connecting to {conn.Name}...";

        // Try registry for already-resolved connection string
        var managed = _registry.GetById(conn.Id);
        string? connStr = managed?.ResolvedConnectionString;

        if (connStr == null)
        {
            // Not connected via registry — try to connect
            var (success, error) = await _registry.ConnectAsync(conn.Id);
            if (!success)
            {
                // Fall back to Connection Dialog
                _viewModel.StatusMessage = $"Failed: {error}";
                await ChangeConnectionAsync();
                return;
            }
            connStr = _registry.GetConnectionString(conn.Id);
        }

        if (connStr == null)
        {
            _viewModel.StatusMessage = $"Failed to connect to {conn.Name}";
            return;
        }

        // Success — switch to Query Editor
        QueryEditorTab.IsChecked = true;
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        if (host != null)
        {
            host.AddNewTab(connStr, conn);
        }

        _viewModel.StatusMessage = $"Connected to {conn.Name}";
        UpdateStatusBar();
    }
}
