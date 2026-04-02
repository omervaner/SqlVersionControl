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
    private readonly ConnectionRegistry _registry;
    private readonly SessionService _sessionService;
    private readonly QueryFileService _queryFileService;
    private readonly SleepDetector _sleepDetector;
    private UpdateService? _updateService;
    private bool _isOffline;
    private string? _lastConnectionColor;
    private string? _lastConnectionDisplay;

    public SettingsService AppSettings => _settings;

    public MainWindow()
    {
        InitializeComponent();

        // Extend into title bar on macOS only (traffic light integration)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 28;
        }

        _settings = new SettingsService();
        _registry = new ConnectionRegistry(_settings);
        _registry.Load();
        _registry.ConnectionAdded += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildQuickSwitchButtons());
        _registry.ConnectionRemoved += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildQuickSwitchButtons());
        _registry.ConnectionStateChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildQuickSwitchButtons());
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
            compareView.Initialize(_settings, _registry);
            compareView.ViewModel.DeployRequested += OnDeployRequested;
            compareView.NewConnectionRequested += () => _ = OnMenuManageConnectionsAsync();
            compareView.RefreshTheme();
        }

        // Initialize ActivityView with shared services
        var activityView = this.FindControl<ActivityView>("ActivityViewControl");
        activityView?.Initialize(_viewModel.DatabaseService);

        // Initialize TraceView with registry
        var traceView = this.FindControl<TraceView>("TraceViewControl");
        traceView?.Initialize(_registry);
        if (traceView != null)
            traceView.NewConnectionRequested += () => _ = OnMenuManageConnectionsAsync();

        // Initialize QueryEditorHost with shared services
        var qeHost = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        qeHost?.Initialize(_viewModel.DatabaseService, _viewModel, _sessionService, _settings, _registry);
        if (qeHost != null)
        {
            qeHost.ActiveTabChanged += () => { UpdateStatusBar(); };
            qeHost.CaretPositionChanged += (line, col) =>
            {
                CursorPositionText.Text = $"Ln {line}, Col {col}";
                CursorPositionText.IsVisible = QueryEditorTab.IsChecked == true;
            };
            qeHost.NewConnectionRequested += async () => await OnMenuManageConnectionsAsync();
            qeHost.SessionRestoreWarning += msg => _viewModel.StatusMessage = msg;
        }

        // Enable window dragging from title bar area (macOS only — Windows has its own title bar)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            TitleBarBorder.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        // Wire up dependencies button
        DependenciesButton.Click += async (s, e) => await ShowDependenciesAsync();

        // Wire up settings button
        SettingsButton.Click += async (s, e) => await ShowSettingsDialogAsync();

        // Wire up History connection indicator
        HistoryConnectionIndicator.Initialize(_registry);
        HistoryConnectionIndicator.ConnectionSelected += async (managed) =>
        {
            if (!managed.IsConnected)
                await _registry.ConnectAsync(managed.Id);
            if (managed.IsConnected && managed.ResolvedConnectionString != null)
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(managed.ResolvedConnectionString);
                var settings = new ConnectionSettings
                {
                    Server = managed.Config.Server,
                    Database = managed.Config.Database,
                    Username = managed.Config.Username,
                    Password = builder.Password,
                    UseWindowsAuth = managed.Config.UseWindowsAuth,
                    TrustServerCertificate = managed.Config.TrustServerCertificate
                };
                _viewModel.SetHistoryConnection(settings, managed.Config);
                HistoryConnectionIndicator.SetActiveConnection(managed);
                UpdateStatusBar();
            }
        };
        HistoryConnectionIndicator.NewConnectionRequested += () => _ = OnMenuManageConnectionsAsync();

        // Wire up Activity connection indicator
        var actView = this.FindControl<ActivityView>("ActivityViewControl");
        if (actView != null)
        {
            actView.ConnectionIndicator.Initialize(_registry);
            actView.ConnectionIndicator.ConnectionSelected += async (managed) =>
            {
                if (!managed.IsConnected)
                    await _registry.ConnectAsync(managed.Id);
                if (managed.IsConnected && managed.ResolvedConnectionString != null)
                {
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(managed.ResolvedConnectionString);
                    var settings = new ConnectionSettings
                    {
                        Server = managed.Config.Server,
                        Database = managed.Config.Database,
                        Username = managed.Config.Username,
                        Password = builder.Password,
                        UseWindowsAuth = managed.Config.UseWindowsAuth,
                        TrustServerCertificate = managed.Config.TrustServerCertificate
                    };
                    _viewModel.SetActivityConnection(settings, managed.Config);
                    actView.ConnectionIndicator.SetActiveConnection(managed);
                    await actView.InitializeConnectionAsync(managed.ResolvedConnectionString);
                    UpdateStatusBar();
                }
            };
            actView.ConnectionIndicator.NewConnectionRequested += () => _ = OnMenuManageConnectionsAsync();

            // Wire "Open in Editor" from Activity view
            if (actView.ViewModel != null)
            {
                actView.ViewModel.OpenInEditorRequested += script =>
                {
                    var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                    host?.OpenScriptInNewTab(script);
                };
            }
        }

        // Wire up reconnect overlay buttons
        RetryButton.Click += async (_, _) => await ReconnectAsync();
        DismissButton.Click += (_, _) => DismissReconnectOverlay();
        ReconnectNowButton.Click += async (_, _) => await ReconnectAsync();

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
        ActivityTab.Click += (_, _) => UpdateStatusBar();

        // Wire update bar buttons
        UpdateNowButton.Click += OnUpdateNowClicked;
        UpdateLaterButton.Click += (_, _) => UpdateBar.IsVisible = false;

        // Wire crash banner buttons
        CrashViewButton.Click += OnCrashViewClicked;
        CrashCopyButton.Click += OnCrashCopyClicked;
        CrashDismissButton.Click += (_, _) =>
        {
            CrashLogger.ClearCrashReports();
            CrashBanner.IsVisible = false;
        };

        // Check for crash reports from previous session
        if (CrashLogger.HasPendingCrashReports())
            CrashBanner.IsVisible = true;

        // Command Palette wiring
        CommandPaletteInput.TextChanged += (_, _) => FilterCommandPalette(CommandPaletteInput.Text ?? "");
        CommandPaletteInput.KeyDown += OnCommandPaletteKeyDown;
        CommandPaletteList.DoubleTapped += (_, _) => ExecuteSelectedCommand();
        CommandPaletteBackdrop.PointerPressed += (_, _) => HideCommandPalette();

        // Check for updates (non-blocking)
        _ = CheckForUpdatesAsync();

        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private AvaloniaEdit.TextEditor? GetActiveEditor()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        return host?.GetActiveEditor();
    }

    private QueryTabView? GetActiveQueryTabView()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        return host?.GetActiveTabView();
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

    private string? GetActiveConnectionString()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        return host?.ActiveTabViewModel?.TabConnectionString;
    }

    private void FormatSqlInEditor()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        host?.FormatSqlInEditor();
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

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Auto-stop any active trace recording to avoid orphaned XE sessions
        var traceView = this.FindControl<TraceView>("TraceViewControl");
        if (traceView?.DataContext is TraceViewModel traceVm && traceVm.State == TraceState.Recording)
        {
            try { await traceVm.StopCaptureCommand.ExecuteAsync(null); }
            catch { /* best-effort cleanup */ }
        }

        _sleepDetector.Stop();

        // Stop timers
        _viewModel.StopAutoSyncTimer();
        var activityView = this.FindControl<ActivityView>("ActivityViewControl");
        activityView?.ViewModel?.Dispose();

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

        // Try auto-connecting saved connections with ConnectOnStartup
        var autoConnects = _registry.Connections
            .Where(c => c.Config.ConnectOnStartup)
            .ToList();

        foreach (var managed in autoConnects)
        {
            var (success, _) = await _registry.ConnectAsync(managed.Id);
            if (success && managed.ResolvedConnectionString != null)
            {
                // Build ConnectionSettings from the managed connection
                var config = managed.Config;
                var settings = new ConnectionSettings
                {
                    Server = config.Server,
                    Database = config.Database,
                    Username = config.Username,
                    UseWindowsAuth = config.UseWindowsAuth,
                    TrustServerCertificate = config.TrustServerCertificate,
                };
                // Use the resolved connection string's password if SQL auth
                if (!config.UseWindowsAuth)
                    settings.Password = PasswordStore.Get(config.Server, config.Database, config.Username) ?? "";

                _viewModel.OnConnected(settings, config);
                _sleepDetector.Start();

                var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                host?.SetDefaultConnection(settings, config);

                UpdateStatusBar();
                UpdateHistoryConnectionIndicator();

                if (host != null) _ = host.ReloadDatabasesAsync();

                UpdateActivityConnectionIndicator();
                var actView = this.FindControl<ActivityView>("ActivityViewControl");
                if (actView != null)
                    _ = actView.InitializeConnectionAsync(settings.ConnectionString);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var traceService = new TraceService();
                        await traceService.CleanupOrphanedSessionsAsync(settings.ConnectionString);
                    }
                    catch { }
                });

                var traceView = this.FindControl<TraceView>("TraceViewControl");
                traceView?.RefreshConnections(_registry);

                return; // Skip the connection dialog
            }
        }

        // No auto-connect succeeded — show dialog as usual
        await ShowConnectionDialogAsync();
    }

    private async Task ShowSettingsDialogAsync()
    {
        var connStr = GetActiveConnectionString();
        var dialog = new SettingsDialog(_settings, RefreshDiffViews,
            _viewModel.DatabaseService, connStr);
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

    private void UpdateHistoryConnectionIndicator()
    {
        var profile = _viewModel.HistoryConnectionProfile;
        HistoryConnectionIndicator.SetActiveConnection(profile);
    }

    private void UpdateActivityConnectionIndicator()
    {
        var actView = this.FindControl<ActivityView>("ActivityViewControl");
        var profile = _viewModel.ActivityConnectionProfile;
        actView?.ConnectionIndicator.SetActiveConnection(profile);
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
