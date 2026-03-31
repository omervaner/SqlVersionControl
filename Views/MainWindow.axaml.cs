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
            compareView.RefreshTheme();
        }

        // Initialize PlanView with shared services
        var planView = this.FindControl<PlanView>("PlanViewControl");
        planView?.Initialize(_viewModel.DatabaseService, _viewModel);

        // Initialize ActivityView with shared services
        var activityView = this.FindControl<ActivityView>("ActivityViewControl");
        activityView?.Initialize(_viewModel.DatabaseService);

        // Initialize TraceView with registry
        var traceView = this.FindControl<TraceView>("TraceViewControl");
        traceView?.Initialize(_registry);

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
        }

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

        // Wire up History connection button
        HistoryConnectionButton.Click += async (s, e) => await ChangeHistoryConnectionAsync();

        // Wire up Activity connection button
        var actView = this.FindControl<ActivityView>("ActivityViewControl");
        if (actView != null)
        {
            actView.FindControl<Button>("ActivityConnectionButton")!.Click += async (s, e) =>
                await ChangeActivityConnectionAsync();
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

    private void WireMenuItems()
    {
        // File menu
        MenuNewQuery.Click += (_, _) => OnMenuNewQuery();
        MenuOpenFile.Click += async (_, _) => await OnMenuOpenFileAsync();
        MenuSave.Click += async (_, _) => await OnMenuSaveAsync();
        MenuSaveAs.Click += async (_, _) => await OnMenuSaveAsAsync();
        MenuChangeDb.Click += async (_, _) => await OnMenuChangeDatabaseAsync();
        MenuManageConnections.Click += async (_, _) => await OnMenuManageConnectionsAsync();
        MenuExportGit.Click += async (_, _) => await ExportToGitAsync();
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
        MenuComment.Click += (_, _) => GetActiveQueryTabView()?.CommentLines();
        MenuUncomment.Click += (_, _) => GetActiveQueryTabView()?.UncommentLines();
        MenuGoToLine.Click += (_, _) => GetActiveQueryTabView()?.ShowGoToLinePopup();
        MenuSelectAll.Click += (_, _) => GetActiveEditor()?.SelectAll();
        MenuToggleWordWrap.Click += (_, _) =>
        {
            var editor = GetActiveEditor();
            if (editor != null) editor.WordWrap = !editor.WordWrap;
        };

        // View menu
        MenuToggleOE.Click += (_, _) =>
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.ToggleObjectExplorer();
        };
        MenuToggleResults.Click += (_, _) =>
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.ToggleActiveResultsPanel();
        };
        MenuZoomIn.Click += (_, _) =>
        {
            var newSize = Math.Min(_settings.Settings.FontSize + 1, 32);
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
        };
        MenuZoomOut.Click += (_, _) =>
        {
            var newSize = Math.Max(_settings.Settings.FontSize - 1, 8);
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
        };
        MenuZoomReset.Click += (_, _) =>
        {
            _settings.Settings.FontSize = 12;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, 12);
            _settings.Save();
        };
        MenuToggleTheme.Click += (_, _) =>
        {
            _settings.Settings.UseDarkTheme = !_settings.Settings.UseDarkTheme;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize);
            _settings.Save();
        };
        MenuViewWordWrap.Click += (_, _) =>
        {
            var editor = GetActiveEditor();
            if (editor != null) editor.WordWrap = !editor.WordWrap;
        };

        // Tools menu
        MenuFormatSql.Click += (_, _) => FormatSqlInEditor();
        MenuSqlQuoter.Click += async (_, _) => await ShowSqlQuoterDialogAsync();
        MenuTextCompare.Click += async (_, _) => await new TextCompareDialog().ShowDialog(this);
        MenuIndexAnalysis.Click += async (_, _) => await ShowIndexAnalysisDialogAsync();

        // Help menu
        MenuKeyboardShortcuts.Click += async (_, _) => await new KeyboardShortcutsDialog().ShowDialog(this);
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

    private async Task OnMenuManageConnectionsAsync()
    {
        var dialog = new ConnectionManagerDialog(_registry, _viewModel.DatabaseService);
        await dialog.ShowDialog(this);
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
            var dir = Path.GetDirectoryName(path);
            var shortDir = dir != null ? $"  ({Path.GetFileName(dir)})" : "";
            var item = new MenuItem { Header = $"{name}{shortDir}", Tag = path };
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
            // Strip leading whitespace, blank lines, and comment lines for better preview
            var lines = entry.SqlText.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("--"));
            var truncated = string.Join(" ", lines);
            if (truncated.Length > 120)
                truncated = truncated[..117] + "...";

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
        // Escape dismisses overlays
        if (e.Key == Key.Escape && CommandPaletteOverlay.IsVisible)
        {
            HideCommandPalette();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ReconnectOverlay.IsVisible)
        {
            DismissReconnectOverlay();
            e.Handled = true;
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // Ctrl+Tab / Ctrl+Shift+Tab — switch query tabs
        if (ctrl && e.Key == Key.Tab && QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host != null)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    host.SwitchToPreviousTab();
                else
                    host.SwitchToNextTab();
            }
            e.Handled = true;
            return;
        }

        // Cmd+E / Ctrl+E — Command Palette
        if (ctrl && e.Key == Key.E)
        {
            ShowCommandPalette();
            e.Handled = true;
            return;
        }

        // Cmd+? / Ctrl+? — Keyboard Shortcuts dialog
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.OemQuestion)
        {
            _ = new KeyboardShortcutsDialog().ShowDialog(this);
            e.Handled = true;
            return;
        }

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

        // Redo: Ctrl+Y or Cmd/Ctrl+Shift+Z
        if (ctrl && (e.Key == Key.Y || (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Z)))
        {
            var editor = GetActiveEditor();
            if (editor?.Document.UndoStack.CanRedo == true)
            {
                editor.Document.UndoStack.Redo();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Shift+F — Format SQL
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.F && QueryEditorTab.IsChecked == true)
        {
            FormatSqlInEditor();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Q — Quick Quote selection
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Q && QueryEditorTab.IsChecked == true)
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            host?.QuickQuoteSelection(nPrefix: false);
            e.Handled = true;
            return;
        }

        // Editor shortcuts: Alt+Up/Down (move lines), Ctrl+G (go to line),
        // Ctrl+K (comment), Ctrl+L (uncomment), Ctrl+Shift+U (upper), Ctrl+Shift+L (lower), Alt+Z (word wrap)
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (QueryEditorTab.IsChecked == true &&
            ((alt && (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Z)) ||
             (ctrl && e.Key == Key.G) ||
             (ctrl && !shift && (e.Key == Key.K || e.Key == Key.L)) ||
             (ctrl && shift && (e.Key == Key.U || e.Key == Key.L))))
        {
            var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
            if (host?.HandleKeyDown(e) == true)
                e.Handled = true;
            return;
        }

        // Cmd+= / Cmd+- — font zoom
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            var newSize = Math.Min(_settings.Settings.FontSize + 1, 32);
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            var newSize = Math.Max(_settings.Settings.FontSize - 1, 8);
            _settings.Settings.FontSize = newSize;
            ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, newSize);
            _settings.Save();
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

            case Key.D5:
                ActivityTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D6:
                TraceTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.T when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // Cmd+Shift+T — toggle dark/light theme
                _settings.Settings.UseDarkTheme = !_settings.Settings.UseDarkTheme;
                ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize);
                _settings.Save();
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
                // Cancel running query on Query Editor tab
                if (QueryEditorTab.IsChecked == true)
                {
                    var qeHost = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                    var activeTab = qeHost?.ActiveTabViewModel;
                    if (activeTab?.IsRunning == true)
                    {
                        activeTab.StopQueryCommand.Execute(null);
                        e.Handled = true;
                        break;
                    }
                }

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
                UpdateHistoryConnectionDot();

                if (host != null) _ = host.ReloadDatabasesAsync();

                var actView = this.FindControl<ActivityView>("ActivityViewControl");
                actView?.UpdateConnectionDot(
                    _viewModel.ActivityConnectionColor,
                    _viewModel.ActivityConnectionProfile?.Name);
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
            UpdateHistoryConnectionDot();

            // Load databases into Query Editor Host
            if (host != null) _ = host.ReloadDatabasesAsync();

            // Initialize Activity view with this connection
            var actView = this.FindControl<ActivityView>("ActivityViewControl");
            actView?.UpdateConnectionDot(
                _viewModel.ActivityConnectionColor,
                _viewModel.ActivityConnectionProfile?.Name);
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
                catch { }
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

    private async Task ShowSettingsDialogAsync()
    {
        var connStr = GetActiveConnectionString();
        var dialog = new SettingsDialog(_settings, RefreshDiffViews,
            _viewModel.DatabaseService, connStr);
        await dialog.ShowDialog(this);
    }

    private async Task ExportToGitAsync()
    {
        var exportPath = _settings.Settings.GitExportPath;
        if (string.IsNullOrEmpty(exportPath))
        {
            // No export path configured — open Settings dialog instead
            await ShowSettingsDialogAsync();
            return;
        }

        var connStr = GetActiveConnectionString();
        if (connStr == null)
            return;

        var dialog = new ExportProgressDialog();
        var showTask = dialog.ShowDialog(this);
        await dialog.RunExportAsync(_viewModel.DatabaseService, connStr, exportPath,
            _settings.Settings.GitIncludeSystemDatabases);
        await showTask;
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

    private async Task ShowSqlQuoterDialogAsync()
    {
        var dialog = new SqlQuoterDialog();
        await dialog.ShowDialog(this);
    }

    private async Task ShowIndexAnalysisDialogAsync()
    {
        if (!_viewModel.DatabaseService.IsConnected) return;

        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var activeVm = host?.ActiveTabViewModel;
        var connStr = activeVm?.TabConnectionString;
        if (connStr == null) return;

        var databases = await _viewModel.DatabaseService.GetDatabasesAsync(connStr);
        var currentDb = activeVm?.SelectedDatabase;

        var dialog = new IndexAnalysisDialog(_viewModel.DatabaseService, connStr, databases, currentDb);
        dialog.OnScriptGenerated += script =>
        {
            host?.OpenScriptInNewTab(script, activeVm?.TabConnectionString, activeVm?.TabConnectionProfile);
        };

        // Save main window position — macOS nudges the owner when showing large modal dialogs
        var savedPos = Position;
        await dialog.ShowDialog(this);
        Position = savedPos;
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
            UpdateHistoryConnectionDot();

            // Reload databases into Query Editor Host
            if (host != null) _ = host.ReloadDatabasesAsync();
        }
    }

    private async Task ChangeHistoryConnectionAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings, _registry);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            _viewModel.SetHistoryConnection(dialog.Result, dialog.ResultConnection);
            UpdateStatusBar();
            UpdateHistoryConnectionDot();
        }
    }

    private void UpdateHistoryConnectionDot()
    {
        var color = Avalonia.Media.Color.Parse(_viewModel.HistoryConnectionColor);
        HistoryConnectionDot.Fill = new Avalonia.Media.SolidColorBrush(color);
        var profile = _viewModel.HistoryConnectionProfile;
        HistoryConnectionButton.Content = profile?.Name ?? "Connection";
    }

    private async Task ChangeActivityConnectionAsync()
    {
        var dialog = new ConnectionDialog(_viewModel.DatabaseService, _settings, _registry);
        await dialog.ShowDialog(this);

        if (dialog.Result != null)
        {
            _viewModel.SetActivityConnection(dialog.Result, dialog.ResultConnection);
            UpdateStatusBar();

            // Update Activity view's connection dot and initialize
            var actView = this.FindControl<ActivityView>("ActivityViewControl");
            actView?.UpdateConnectionDot(
                _viewModel.ActivityConnectionColor,
                _viewModel.ActivityConnectionProfile?.Name);
            if (actView != null)
                await actView.InitializeConnectionAsync(dialog.Result.ConnectionString);
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

    // ── Command Palette ─────────────────────────────────────────────

    private List<CommandPaletteItem>? _allCommands;

    private List<CommandPaletteItem> BuildCommandRegistry()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var isMac = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
        var mod = isMac ? "Cmd" : "Ctrl";

        return
        [
            // File
            new() { Name = "New Query", Shortcut = $"{mod}+N", Description = "Open a new query tab", Execute = OnMenuNewQuery },
            new() { Name = "Open File", Shortcut = $"{mod}+O", Description = "Open a saved query", Execute = () => _ = OnMenuOpenFileAsync() },
            new() { Name = "Save", Shortcut = $"{mod}+S", Description = "Save current query", Execute = () => _ = OnMenuSaveAsync() },
            new() { Name = "Save As", Shortcut = $"{mod}+Shift+S", Description = "Save query as new file", Execute = () => _ = OnMenuSaveAsAsync() },
            new() { Name = "Manage Connections", Shortcut = $"{mod}+Shift+M", Description = "Open connection manager", Execute = () => _ = OnMenuManageConnectionsAsync() },
            new() { Name = "Export to Git", Shortcut = "", Description = "Export database objects to git", Execute = () => _ = ExportToGitAsync() },

            // Edit
            new() { Name = "Find", Shortcut = $"{mod}+F", Description = "Find in editor", Execute = () => OpenSearchPanel(false) },
            new() { Name = "Replace", Shortcut = $"{mod}+H", Description = "Find and replace", Execute = () => OpenSearchPanel(true) },
            new() { Name = "Go to Line", Shortcut = $"{mod}+G", Description = "Jump to line number", Execute = () => GetActiveQueryTabView()?.ShowGoToLinePopup() },
            new() { Name = "Comment Lines", Shortcut = $"{mod}+K", Description = "Toggle line comments", Execute = () => GetActiveQueryTabView()?.CommentLines() },
            new() { Name = "Uncomment Lines", Shortcut = $"{mod}+L", Description = "Remove line comments", Execute = () => GetActiveQueryTabView()?.UncommentLines() },
            new() { Name = "Uppercase Selection", Shortcut = $"{mod}+Shift+U", Description = "Transform to uppercase", Execute = () => GetActiveQueryTabView()?.UppercaseSelection() },
            new() { Name = "Lowercase Selection", Shortcut = $"{mod}+Shift+L", Description = "Transform to lowercase", Execute = () => GetActiveQueryTabView()?.LowercaseSelection() },
            new() { Name = "Toggle Word Wrap", Shortcut = "Alt+Z", Description = "Wrap long lines", Execute = () => { var e = GetActiveEditor(); if (e != null) e.WordWrap = !e.WordWrap; } },

            // View
            new() { Name = "Toggle Object Explorer", Shortcut = $"{mod}+B", Description = "Show/hide sidebar", Execute = () => host?.ToggleObjectExplorer() },
            new() { Name = "Toggle Results Panel", Shortcut = $"{mod}+J", Description = "Show/hide results", Execute = () => host?.ToggleActiveResultsPanel() },
            new() { Name = "Zoom In", Shortcut = $"{mod}+=", Description = "Increase font size", Execute = () => { var s = Math.Min(_settings.Settings.FontSize + 1, 32); _settings.Settings.FontSize = s; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, s); _settings.Save(); } },
            new() { Name = "Zoom Out", Shortcut = $"{mod}+-", Description = "Decrease font size", Execute = () => { var s = Math.Max(_settings.Settings.FontSize - 1, 8); _settings.Settings.FontSize = s; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, s); _settings.Save(); } },
            new() { Name = "Reset Zoom", Shortcut = "", Description = "Reset to default font size", Execute = () => { _settings.Settings.FontSize = 12; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, 12); _settings.Save(); } },
            new() { Name = "Toggle Dark/Light Theme", Shortcut = $"{mod}+Shift+T", Description = "Switch theme", Execute = () => { _settings.Settings.UseDarkTheme = !_settings.Settings.UseDarkTheme; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize); _settings.Save(); } },

            // Tools
            new() { Name = "Format SQL", Shortcut = "Ctrl+Shift+F", Description = "Format selected SQL", Execute = () => host?.FormatSqlInEditor() },
            new() { Name = "Quick Quote Selection", Shortcut = "Ctrl+Shift+Q", Description = "Quote selected text", Execute = () => host?.QuickQuoteSelection(false) },
            new() { Name = "SQL Quoter", Shortcut = "", Description = "Open SQL Quoter dialog", Execute = () => _ = ShowSqlQuoterDialogAsync() },
            new() { Name = "Text Compare", Shortcut = "", Description = "Compare two text blocks", Execute = () => _ = new TextCompareDialog().ShowDialog(this) },
            new() { Name = "Index Analysis", Shortcut = "", Description = "Analyze unused/missing indexes", Execute = () => _ = ShowIndexAnalysisDialogAsync() },

            // Tabs
            new() { Name = "Close Tab", Shortcut = $"{mod}+W", Description = "Close active query tab", Execute = () => { if (host != null) _ = host.CloseActiveTabAsync(); } },

            // Navigation
            new() { Name = "Query Editor", Shortcut = $"{mod}+1", Description = "Switch to editor", Execute = () => QueryEditorTab.IsChecked = true },
            new() { Name = "Version History", Shortcut = $"{mod}+2", Description = "Switch to history", Execute = () => VersionHistoryTab.IsChecked = true },
            new() { Name = "Compare Databases", Shortcut = $"{mod}+3", Description = "Switch to compare", Execute = () => CompareTab.IsChecked = true },
            new() { Name = "Execution Plan", Shortcut = $"{mod}+4", Description = "Switch to plan", Execute = () => PlanTab.IsChecked = true },
            new() { Name = "Activity Monitor", Shortcut = $"{mod}+5", Description = "Switch to activity", Execute = () => ActivityTab.IsChecked = true },
            new() { Name = "Query Trace", Shortcut = $"{mod}+6", Description = "Switch to trace", Execute = () => TraceTab.IsChecked = true },

            // Run
            new() { Name = "Run Query", Shortcut = "F5", Description = "Execute query", Execute = () => { } }, // handled by F5 key
            new() { Name = "Run with Trace", Shortcut = "Ctrl+Shift+F5", Description = "Execute with XE tracing", Execute = () => { } },

            // Help
            new() { Name = "Keyboard Shortcuts", Shortcut = "", Description = "View all shortcuts", Execute = () => _ = new KeyboardShortcutsDialog().ShowDialog(this) },
            new() { Name = "About Lookout", Shortcut = "", Description = "Version info", Execute = () => _ = ShowAboutDialogAsync() },
            new() { Name = "Settings", Shortcut = "", Description = "Open settings", Execute = () => _ = ShowSettingsDialogAsync() },
        ];
    }

    private void OnCommandPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ExecuteSelectedCommand();
                e.Handled = true;
                break;
            case Key.Escape:
                HideCommandPalette();
                e.Handled = true;
                break;
            case Key.Down:
                if (CommandPaletteList.SelectedIndex < (CommandPaletteList.ItemCount - 1))
                    CommandPaletteList.SelectedIndex++;
                e.Handled = true;
                break;
            case Key.Up:
                if (CommandPaletteList.SelectedIndex > 0)
                    CommandPaletteList.SelectedIndex--;
                e.Handled = true;
                break;
        }
    }

    private void ShowCommandPalette()
    {
        _allCommands ??= BuildCommandRegistry();

        CommandPaletteInput.Text = "";
        CommandPaletteList.ItemsSource = _allCommands;
        CommandPaletteList.SelectedIndex = 0;
        CommandPaletteOverlay.IsVisible = true;
        CommandPaletteInput.Focus();
    }

    private void HideCommandPalette()
    {
        CommandPaletteOverlay.IsVisible = false;
        // Return focus to editor
        GetActiveEditor()?.Focus();
    }

    private void FilterCommandPalette(string query)
    {
        if (_allCommands == null) return;

        var filtered = _allCommands
            .Select(c => (item: c, score: c.FuzzyMatch(query)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.item)
            .ToList();

        CommandPaletteList.ItemsSource = filtered;
        CommandPaletteList.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private void ExecuteSelectedCommand()
    {
        if (CommandPaletteList.SelectedItem is CommandPaletteItem item)
        {
            HideCommandPalette();
            item.Execute();
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
        var isHistory = VersionHistoryTab.IsChecked == true;
        var isCompare = CompareTab.IsChecked == true;
        var isPlan = PlanTab.IsChecked == true;
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
            else if (isPlan)
            {
                displayColor = _viewModel.PlanConnectionColor;
                displayText = _viewModel.PlanConnectionDisplay;
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
