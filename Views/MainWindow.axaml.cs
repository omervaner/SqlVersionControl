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
    private readonly SessionService _sessionService;
    private readonly QueryFileService _queryFileService;
    private readonly SleepDetector _sleepDetector;
    private UpdateService? _updateService;

    public SettingsService AppSettings => _settings;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new SettingsService();
        _sessionService = new SessionService();
        _queryFileService = new QueryFileService();
        _viewModel = new MainWindowViewModel();
        _viewModel.AppSettings = _settings;
        DataContext = _viewModel;

        // Apply saved theme, font size, and grid row height
        ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize);
        ThemeManager.ApplyGridRowHeight(_settings.Settings.GridRowHeight);

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
        qeHost?.Initialize(_viewModel.DatabaseService, _viewModel, _sessionService, _settings);
        if (qeHost != null)
            qeHost.ActiveTabChanged += () => { UpdateStatusBar(); };

        // Enable window dragging from title bar area (empty space not consumed by menus/buttons)
        TitleBarBorder.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        // Wire up dependencies button
        DependenciesButton.Click += async (s, e) => await ShowDependenciesAsync();

        // Wire up settings button
        SettingsButton.Click += async (s, e) => await ShowSettingsDialogAsync();

        // Wire up retry button on reconnect overlay
        RetryButton.Click += async (s, e) => await ReconnectAsync();

        // Wire menu items
        WireMenuItems();

        // Sleep/wake detection
        _sleepDetector = new SleepDetector();
        _sleepDetector.WokeFromSleep += OnWokeFromSleep;

        // Re-apply code-behind colors on theme change
        ThemeManager.ThemeChanged += () =>
        {
            UpdateStatusBar();
            MainDiffView.ApplyTheme();
            this.FindControl<CompareView>("CompareViewControl")?.RefreshTheme();
            this.FindControl<QueryEditorHost>("QueryEditorHostControl")?.RefreshTheme();
        };

        // Status bar: track tab switches and connection changes
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        QueryEditorTab.Click += (_, _) => UpdateStatusBar();
        VersionHistoryTab.Click += (_, _) => UpdateStatusBar();
        CompareTab.Click += (_, _) => UpdateStatusBar();
        PlanTab.Click += (_, _) => UpdateStatusBar();

        // Wire update bar buttons
        UpdateNowButton.Click += OnUpdateNowClicked;
        UpdateLaterButton.Click += (_, _) => UpdateBar.IsVisible = false;

        // Check for updates (non-blocking)
        _ = CheckForUpdatesAsync();

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void WireMenuItems()
    {
        // File menu
        MenuNewQuery.Click += (_, _) => OnMenuNewQuery();
        MenuOpenFile.Click += async (_, _) => await OnMenuOpenFileAsync();
        MenuSave.Click += async (_, _) => await OnMenuSaveAsync();
        MenuSaveAs.Click += async (_, _) => await OnMenuSaveAsAsync();
        MenuExit.Click += (_, _) => Close();

        // Populate Recent Files and Query History submenus
        RebuildRecentFilesMenu();
        RebuildQueryHistoryMenu();

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
        MenuChangeDb.Click += async (_, _) => await OnMenuChangeDatabaseAsync();

        // Help menu
        MenuAbout.Click += async (_, _) => await ShowAboutDialogAsync();
        MenuCheckUpdates.Click += async (_, _) =>
        {
            _updateService ??= new UpdateService();

            var hasUpdate = await _updateService.CheckForUpdateAsync();
            if (hasUpdate)
            {
                UpdateText.Text = $"Version {_updateService.AvailableVersion} is available";
                UpdateBar.IsVisible = true;
            }
            else
            {
                // No update found — show releases page with context
                UpdateText.Text = "You're on the latest version. Opening releases page...";
                UpdateBar.IsVisible = true;
                OpenUrl("https://github.com/omervaner/SqlVersionControl/releases");
            }
        };
    }

    private async Task OnMenuChangeDatabaseAsync()
    {
        await ChangeConnectionAsync();
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

    private async Task OnMenuOpenFileAsync()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        if (host == null) return;

        QueryEditorTab.IsChecked = true;
        await host.OpenQueryAsync(_queryFileService, _settings);
        RebuildRecentFilesMenu();
    }

    private async Task OnMenuSaveAsync()
    {
        if (QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host != null)
            {
                await host.SaveActiveQueryAsync(_queryFileService, _settings);
                RebuildRecentFilesMenu();
            }
        }
        else if (_viewModel.IsConnected)
        {
            // Non-query tab: keep existing DDL sync behavior
            _ = _viewModel.SyncCommand.ExecuteAsync(null);
        }
    }

    private async Task OnMenuSaveAsAsync()
    {
        if (QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host != null)
            {
                await host.SaveAsActiveQueryAsync(_queryFileService, _settings);
                RebuildRecentFilesMenu();
            }
        }
    }

    private void RebuildRecentFilesMenu()
    {
        MenuRecentFiles.Items.Clear();
        var recentPaths = _settings.GetRecentQueries();

        if (recentPaths.Count == 0)
        {
            var empty = new MenuItem { Header = "(none)", IsEnabled = false };
            MenuRecentFiles.Items.Add(empty);
            return;
        }

        foreach (var path in recentPaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var item = new MenuItem { Header = name, Tag = path };
            item.Click += (s, _) =>
            {
                if (s is MenuItem mi && mi.Tag is string filePath)
                {
                    var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                    if (host != null)
                    {
                        QueryEditorTab.IsChecked = true;
                        host.OpenQueryFromPath(filePath, _queryFileService, _settings);
                        RebuildRecentFilesMenu();
                    }
                }
            };
            MenuRecentFiles.Items.Add(item);
        }
    }

    private void RebuildQueryHistoryMenu()
    {
        MenuQueryHistory.Items.Clear();
        var history = _sessionService.GetQueryHistory();

        if (history.Count == 0)
        {
            var empty = new MenuItem { Header = "(none)", IsEnabled = false };
            MenuQueryHistory.Items.Add(empty);
            return;
        }

        foreach (var entry in history)
        {
            var truncated = entry.SqlText.ReplaceLineEndings(" ");
            if (truncated.Length > 80)
                truncated = truncated[..77] + "...";

            var dbLabel = !string.IsNullOrEmpty(entry.Database) ? $" [{entry.Database}]" : "";
            var item = new MenuItem { Header = $"{truncated}{dbLabel}" };
            ToolTip.SetTip(item, entry.SqlText);

            var sql = entry.SqlText;
            var db = entry.Database;
            item.Click += (_, _) =>
            {
                var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                if (host != null)
                {
                    QueryEditorTab.IsChecked = true;
                    host.AddNewTab();
                    if (host.ActiveTabViewModel is { } vm)
                    {
                        var editor = host.GetActiveEditor();
                        if (editor != null)
                        {
                            editor.Text = sql;
                            vm.SetInitialText(sql);
                        }
                        if (db != null && vm.Databases.Contains(db))
                            vm.SelectedDatabase = db;
                    }
                    RebuildQueryHistoryMenu();
                }
            };
            MenuQueryHistory.Items.Add(item);
        }
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
            if (host != null) _ = host.CloseActiveTabAsync();
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

            case Key.B:
            {
                var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                host?.ToggleObjectExplorer();
                e.Handled = true;
                break;
            }

            case Key.J:
                if (QueryEditorTab.IsChecked == true)
                {
                    var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                    host?.ToggleActiveResultsPanel();
                    e.Handled = true;
                }
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

            case Key.O:
                if (QueryEditorTab.IsChecked == true)
                    _ = OnMenuOpenFileAsync();
                e.Handled = true;
                break;

            case Key.R:
                if (_viewModel.IsConnected)
                    _ = _viewModel.RefreshCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case Key.S:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && QueryEditorTab.IsChecked == true)
                {
                    // Ctrl+Shift+S → Save As
                    _ = OnMenuSaveAsAsync();
                }
                else if (QueryEditorTab.IsChecked == true)
                {
                    // Ctrl+S on Query Editor → Save query
                    _ = OnMenuSaveAsync();
                }
                else if (_viewModel.IsConnected)
                {
                    // Ctrl+S on other tabs → Sync from DDL log
                    _ = _viewModel.SyncCommand.ExecuteAsync(null);
                }
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

        // Save session (tabs + query history)
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        host?.SaveSession();

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
            _viewModel.OnConnected(dialog.Result, dialog.ResultConnection);
            _sleepDetector.Start();

            // Set as default connection for new tabs
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.SetDefaultConnection(dialog.Result, dialog.ResultConnection);

            UpdateStatusBar();

            // Load databases into Query Editor Host
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
        var qeHost = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        qeHost?.RefreshTheme();
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
            _viewModel.OnConnected(dialog.Result, dialog.ResultConnection);
            _sleepDetector.Start();

            // Set as default connection for new tabs
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.SetDefaultConnection(dialog.Result, dialog.ResultConnection);

            UpdateStatusBar();

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

        SqlConnection.ClearAllPools();

        for (int i = 1; i <= 3; i++)
        {
            ReconnectText.Text = $"Reconnecting... (attempt {i}/3)";

            if (await _viewModel.DatabaseService.TestConnectionAsync())
            {
                ReconnectOverlay.IsVisible = false;
                _viewModel.StatusMessage = "Reconnected after sleep";

                // Clear per-server caches — tabs will re-validate on next query
                var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                host?.ClearServerCaches();
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

    // ── Status Bar ──────────────────────────────────────────────────

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
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeTabVm = host?.ActiveTabViewModel;

        // Connection indicator — on QE tab, use active tab's connection if set
        if (_viewModel.IsConnected)
        {
            var displayColor = isQE && activeTabVm?.TabConnectionProfile != null
                ? activeTabVm.TabConnectionColor
                : _viewModel.ConnectionColor;
            var displayText = isQE && activeTabVm?.TabConnectionProfile != null
                ? activeTabVm.TabConnectionDisplay
                : _viewModel.ConnectionDisplay;

            var color = Avalonia.Media.Color.Parse(displayColor);
            var brush = new Avalonia.Media.SolidColorBrush(color);
            ConnectionDot.Fill = brush;
            ConnectionText.Text = displayText;
            ConnectionStripe.Background = brush;
            ConnectionStripe.IsVisible = true;

            // Update window title
            this.Title = $"SQL Version Control — {displayText}";
        }
        else
        {
            if (Avalonia.Application.Current?.Resources.TryGetResource("DisconnectedDot", null, out var dotBrush) == true && dotBrush is Avalonia.Media.IBrush disconnectedBrush)
                ConnectionDot.Fill = disconnectedBrush;
            else
                ConnectionDot.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(231, 76, 60));
            ConnectionText.Text = "Disconnected";
            ConnectionStripe.IsVisible = false;
            this.Title = "SQL Version Control";
        }

        // Quick-switch buttons
        RebuildQuickSwitchButtons();

        // Query status section — only visible on Query Editor tab
        QueryStatusSeparator.IsVisible = isQE;
        QueryStatusText.IsVisible = isQE;
        if (!isQE) QueryFlashText.IsVisible = false;

        if (isQE)
            BindActiveQueryTab();
        else
            UnbindQueryTab();
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
        if (e.PropertyName == nameof(QueryTabViewModel.QueryStatusText) && _boundQueryTab != null)
            QueryStatusText.Text = _boundQueryTab.QueryStatusText;
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

    // ── Quick-Switch Buttons ────────────────────────────────────────

    private void RebuildQuickSwitchButtons()
    {
        QuickSwitchPanel.Children.Clear();
        var named = _settings.GetNamedConnections();
        if (named.Count == 0) return;

        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeProfile = host?.ActiveTabViewModel?.TabConnectionProfile;

        foreach (var conn in named)
        {
            var isActive = activeProfile != null &&
                           activeProfile.Server == conn.Server &&
                           activeProfile.Database == conn.Database &&
                           activeProfile.Name == conn.Name;

            var color = Avalonia.Media.Color.Parse(conn.Color ?? "#88a1bb");
            var brush = new Avalonia.Media.SolidColorBrush(color);

            var btn = new Button
            {
                Content = conn.Name,
                FontSize = 10,
                Padding = new Avalonia.Thickness(6, 1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Background = isActive ? brush : Avalonia.Media.Brushes.Transparent,
                Foreground = isActive ? Avalonia.Media.Brushes.White : brush,
                BorderBrush = brush,
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
        // Build connection settings
        var settings = new ConnectionSettings
        {
            Server = conn.Server,
            Database = conn.Database,
            UseWindowsAuth = conn.UseWindowsAuth,
            Username = conn.Username,
        };

        // Look up stored password for SQL auth
        if (!conn.UseWindowsAuth)
        {
            var password = PasswordStore.Get(conn.Server, conn.Database, conn.Username);
            if (string.IsNullOrEmpty(password))
            {
                // No password — fall back to Connection Dialog
                await ChangeConnectionAsync();
                return;
            }
            settings.Password = password;
        }

        // Test connection
        var connStr = settings.ConnectionString;
        _viewModel.StatusMessage = $"Connecting to {conn.Name}...";

        if (!await _viewModel.DatabaseService.TestConnectionAsync(connStr))
        {
            _viewModel.StatusMessage = $"Failed to connect to {conn.Name}";
            return;
        }

        // Success — switch to Query Editor and open a new tab
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
