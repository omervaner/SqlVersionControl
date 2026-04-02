using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class ActivityView : UserControl
{
    private ActivityViewModel? _viewModel;

    public ActivityView()
    {
        InitializeComponent();
    }

    public ActivityViewModel? ViewModel => _viewModel;

    public void Initialize(DatabaseService db)
    {
        _viewModel = new ActivityViewModel(db);
        DataContext = _viewModel;

        // Wire refresh interval combo
        RefreshIntervalCombo.SelectionChanged += (_, _) =>
        {
            if (RefreshIntervalCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out var seconds))
                    _viewModel.AutoRefreshIntervalSeconds = seconds;
            }
        };

        // Wire enabled/outcome filter combos
        EnabledFilterCombo.SelectionChanged += (_, _) =>
        {
            if (EnabledFilterCombo.SelectedItem is ComboBoxItem item)
                _viewModel.JobEnabledFilter = item.Content?.ToString() ?? "All";
        };
        OutcomeFilterCombo.SelectionChanged += (_, _) =>
        {
            if (OutcomeFilterCombo.SelectedItem is ComboBoxItem item)
            {
                var text = item.Content?.ToString() ?? "All outcomes";
                _viewModel.JobOutcomeFilter = text == "All outcomes" ? "All" : text;
            }
        };

        // Wire job action buttons with confirmation
        StartJobButton.Click += async (_, _) => await ConfirmAndStartJobAsync();
        StopJobButton.Click += async (_, _) => await ConfirmAndStopJobAsync();
        ToggleEnabledButton.Click += async (_, _) => await ConfirmAndToggleJobAsync();

        // Wire step action buttons
        DeleteStepButton.Click += async (_, _) => await ConfirmAndDeleteStepAsync();
        RemoveScheduleButton.Click += async (_, _) => await ConfirmAndRemoveScheduleAsync();
        OpenStepButton.Click += (_, _) => _viewModel.OpenStepInEditor(_viewModel.SelectedStep);

        // Wire schedule combo boxes
        FreqTypeCombo.SelectionChanged += (_, _) =>
        {
            if (FreqTypeCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out var ft))
                    _viewModel.EditFreqType = ft;
            }
        };
        SubdayTypeCombo.SelectionChanged += (_, _) =>
        {
            if (SubdayTypeCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                if (int.TryParse(item.Tag.ToString(), out var st))
                    _viewModel.EditFreqSubdayType = st;
            }
        };

        // Escape collapses detail panel
        this.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _viewModel.IsJobDetailVisible)
            {
                _viewModel.CollapseDetail();
                e.Handled = true;
            }
        };

        // Wire session context menu
        SessionsGrid.DoubleTapped += OnSessionDoubleTapped;

        // Build context menus
        BuildSessionContextMenu();
        BuildJobContextMenu();
        BuildJobStepContextMenu();

        // Dynamic card coloring based on health thresholds
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityViewModel.StatCpu))
            {
                var text = _viewModel.StatCpu.Replace("%", "");
                if (int.TryParse(text, out var cpu))
                {
                    CpuValueText.Foreground = cpu > 90 ? (GetBrush("ButtonDanger") ?? Brushes.Red)
                        : cpu > 70 ? (GetBrush("WarningSeverityWarning") ?? Brushes.Orange)
                        : (GetBrush("ScoreHealthy") ?? Brushes.Green);
                }
            }
            else if (e.PropertyName == nameof(ActivityViewModel.StatBufferCache))
            {
                var text = _viewModel.StatBufferCache.Replace("%", "");
                if (double.TryParse(text, out var ratio))
                {
                    BufferCacheValueText.Foreground = ratio < 95 ? (GetBrush("ButtonDanger") ?? Brushes.Red)
                        : ratio < 99 ? (GetBrush("WarningSeverityWarning") ?? Brushes.Orange)
                        : (GetBrush("ScoreHealthy") ?? Brushes.Green);
                }
            }
        };

        // Color coding for grids
        SessionsGrid.LoadingRow += OnSessionRowLoading;
        JobsGrid.LoadingRow += OnJobRowLoading;
        JobHistoryGrid.LoadingRow += OnJobHistoryRowLoading;

        // Theme changes
        ThemeManager.ThemeChanged += () =>
        {
            // Force grid refresh on theme change
            SessionsGrid.InvalidateVisual();
            JobsGrid.InvalidateVisual();
        };
    }

    public async Task InitializeConnectionAsync(string connectionString)
    {
        if (_viewModel != null)
            await _viewModel.InitializeAsync(connectionString);
    }

    public void UpdateConnection(string connectionString)
    {
        _viewModel?.UpdateConnectionString(connectionString);
    }

    public ConnectionIndicator ConnectionIndicator => ActivityConnectionIndicator;

    // ── Context Menus ─────────────────────────────────────────────

    private void BuildSessionContextMenu()
    {
        var menu = new ContextMenu();

        var killItem = new MenuItem { Header = "Kill Session" };
        killItem.Click += async (_, _) => await ConfirmAndKillSessionAsync();
        menu.Items.Add(killItem);

        menu.Items.Add(new Separator());

        var copyItem = new MenuItem { Header = "Copy Query Text" };
        copyItem.Click += async (_, _) => await CopySessionQueryAsync();
        menu.Items.Add(copyItem);

        var openItem = new MenuItem { Header = "Open Query in Editor" };
        openItem.Click += (_, _) => _viewModel?.OpenQueryInEditor(_viewModel.SelectedSession);
        menu.Items.Add(openItem);

        SessionsGrid.ContextMenu = menu;
    }

    private void BuildJobContextMenu()
    {
        var menu = new ContextMenu();

        var startItem = new MenuItem { Header = "Start Job" };
        startItem.Click += async (_, _) => await ConfirmAndStartJobAsync();
        menu.Items.Add(startItem);

        var stopItem = new MenuItem { Header = "Stop Job" };
        stopItem.Click += async (_, _) => await ConfirmAndStopJobAsync();
        menu.Items.Add(stopItem);

        menu.Items.Add(new Separator());

        var toggleItem = new MenuItem { Header = "Enable/Disable" };
        toggleItem.Click += async (_, _) => await ConfirmAndToggleJobAsync();
        menu.Items.Add(toggleItem);

        JobsGrid.ContextMenu = menu;
    }

    private void BuildJobStepContextMenu()
    {
        // Find the steps DataGrid by looking through the visual tree
        // It's not named, so we'll use the JobSteps binding to identify it
        // Actually, let's add the context menu to all DataGrids that show steps
        // For simplicity, we'll wire it up when steps are loaded
    }

    // ── Confirmation Dialogs ──────────────────────────────────────

    private async Task ConfirmAndKillSessionAsync()
    {
        if (_viewModel?.SelectedSession == null) return;
        var session = _viewModel.SelectedSession;

        if (session.IsCurrentSession) return;

        var preview = session.CurrentStatement.Length > 200
            ? session.CurrentStatement[..200] + "..."
            : session.CurrentStatement;

        var dialog = new Window
        {
            Title = "Kill Session",
            Width = 420, Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = BuildConfirmPanel(
                $"Kill session {session.SessionId}?",
                $"Login: {session.Login}\nDatabase: {session.Database}\nElapsed: {session.ElapsedDisplay}\n\n{preview}",
                "Kill", true)
        };

        var result = false;
        ((dialog.Content as Grid)!.Children[2] as StackPanel)!.Children.OfType<Button>()
            .First(b => b.Classes.Contains("btn-primary")).Click += (_, _) => { result = true; dialog.Close(); };
        ((dialog.Content as Grid)!.Children[2] as StackPanel)!.Children.OfType<Button>()
            .Last().Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException());

        if (result)
            await _viewModel.KillSessionAsync(session);
    }

    private async Task ConfirmAndStartJobAsync()
    {
        if (_viewModel?.SelectedJob == null) return;
        var job = _viewModel.SelectedJob;

        var confirmed = await ShowSimpleConfirmAsync($"Start job '{job.JobName}'?", "Start");
        if (confirmed)
            await _viewModel.StartJobAsync(job);
    }

    private async Task ConfirmAndStopJobAsync()
    {
        if (_viewModel?.SelectedJob == null) return;
        var job = _viewModel.SelectedJob;
        if (job.CurrentStatus != "Executing") return;

        var confirmed = await ShowSimpleConfirmAsync($"Stop job '{job.JobName}'?", "Stop");
        if (confirmed)
            await _viewModel.StopJobAsync(job);
    }

    private async Task ConfirmAndDeleteStepAsync()
    {
        if (_viewModel?.SelectedStep == null || _viewModel?.SelectedJob == null) return;
        var step = _viewModel.SelectedStep;

        var confirmed = await ShowSimpleConfirmAsync($"Delete step '{step.StepName}'?", "Delete");
        if (confirmed)
            await _viewModel.DeleteStepAsync();
    }

    private async Task ConfirmAndRemoveScheduleAsync()
    {
        if (_viewModel?.SelectedJob == null) return;
        var jobName = _viewModel.SelectedJob.JobName;

        var confirmed = await ShowSimpleConfirmAsync(
            $"Remove schedule from job '{jobName}'?\nThe job will no longer run automatically.", "Remove");
        if (confirmed)
            await _viewModel.RemoveScheduleAsync();
    }

    private async Task ConfirmAndToggleJobAsync()
    {
        if (_viewModel?.SelectedJob == null) return;
        var job = _viewModel.SelectedJob;

        var action = job.IsEnabled ? "Disable" : "Enable";
        var confirmed = await ShowSimpleConfirmAsync($"{action} job '{job.JobName}'?", action);
        if (confirmed)
            await _viewModel.ToggleJobEnabledAsync(job);
    }

    private async Task<bool> ShowSimpleConfirmAsync(string message, string actionLabel)
    {
        var result = false;
        var dialog = new Window
        {
            Title = "Confirm",
            Width = 360, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = BuildConfirmPanel(message, null, actionLabel, false)
        };

        ((dialog.Content as Grid)!.Children[2] as StackPanel)!.Children.OfType<Button>()
            .First(b => b.Classes.Contains("btn-primary")).Click += (_, _) => { result = true; dialog.Close(); };
        ((dialog.Content as Grid)!.Children[2] as StackPanel)!.Children.OfType<Button>()
            .Last().Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
            if (e.Key == Key.Enter) { result = true; dialog.Close(); }
        };

        await dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException());
        return result;
    }

    private static Grid BuildConfirmPanel(string title, string? detail, string actionLabel, bool isDanger)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(24) };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        grid.Children.Add(titleBlock);
        Grid.SetRow(titleBlock, 0);

        if (detail != null)
        {
            var detailBlock = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
                Opacity = 0.7
            };
            grid.Children.Add(detailBlock);
            Grid.SetRow(detailBlock, 1);
        }

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };

        var actionBtn = new Button { Content = actionLabel, Padding = new Avalonia.Thickness(16, 8) };
        actionBtn.Classes.Add(isDanger ? "btn-primary" : "btn-primary");
        buttons.Children.Add(actionBtn);

        var cancelBtn = new Button { Content = "Cancel", Padding = new Avalonia.Thickness(16, 8) };
        cancelBtn.Classes.Add("btn-secondary");
        buttons.Children.Add(cancelBtn);

        grid.Children.Add(buttons);
        Grid.SetRow(buttons, 2);

        return grid;
    }

    // ── Row Color Coding ──────────────────────────────────────────

    private void OnSessionRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not SessionRow session) return;

        // Opacity for current session
        e.Row.Opacity = session.IsCurrentSession ? 0.5 : 1.0;

        // Row background: blocking highlight > alternating rows
        if (session.BlockingSession > 0)
        {
            e.Row.Background = GetBrush("BlockingHighlight") ?? new SolidColorBrush(Color.FromArgb(30, 255, 100, 100));
        }
        else
        {
            var idx = e.Row.GetIndex();
            e.Row.Background = idx % 2 == 1
                ? (GetBrush("ActivityAlternateRow") ?? Brushes.Transparent)
                : Brushes.Transparent;
        }

        // Defer cell styling to after layout
        e.Row.LayoutUpdated += OnSessionRowLayout;
        void OnSessionRowLayout(object? s, EventArgs args)
        {
            e.Row.LayoutUpdated -= OnSessionRowLayout;
            StyleSessionCells(e.Row, session);
        };
    }

    private void StyleSessionCells(DataGridRow row, SessionRow session)
    {
        var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();

        for (int colIdx = 0; colIdx < cells.Count; colIdx++)
        {
            var cell = cells[colIdx];
            var tb = cell.FindDescendantOfType<TextBlock>();
            if (tb == null) continue;

            // Column indices: 0=SPID, 1=Login, 2=Database, 3=Status, 4=Command,
            // 5=WaitType, 6=BlockedBy, 7=CPU, 8=Elapsed, 9=Statement

            switch (colIdx)
            {
                case 0: // SPID — right-align, semi-bold
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Right;
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.Margin = new Thickness(0, 0, 10, 0);
                    break;

                case 3: // Status — colored pill
                    var status = session.RequestStatus.ToLower();
                    IBrush? pillFg = null, pillBg = null;
                    if (status.Contains("running"))
                    {
                        pillFg = GetBrush("StatusRunning");
                        pillBg = GetBrush("StatusPillRunningBg");
                    }
                    else if (status.Contains("suspend"))
                    {
                        pillFg = GetBrush("StatusSuspended");
                        pillBg = GetBrush("StatusPillSuspendedBg");
                    }
                    else
                    {
                        pillFg = GetBrush("StatusSleeping");
                        pillBg = GetBrush("StatusPillSleepingBg");
                    }
                    if (pillFg != null) tb.Foreground = pillFg;
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Center;
                    if (pillBg != null) cell.Background = pillBg;
                    break;

                case 4: // Command — center-align
                case 5: // Wait Type — center-align
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Center;
                    break;

                case 6: // Blocked By — center-align, red if non-zero
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Center;
                    if (session.BlockingSession > 0)
                    {
                        tb.Foreground = GetBrush("ButtonDanger") ?? Brushes.Red;
                        tb.FontWeight = FontWeight.Bold;
                    }
                    else
                    {
                        tb.Foreground = GetBrush("TextDisabled") ?? tb.Foreground;
                    }
                    break;

                case 7: // CPU — right-align
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Right;
                    tb.Margin = new Thickness(0, 0, 10, 0);
                    break;

                case 8: // Elapsed — right-align
                    tb.TextAlignment = Avalonia.Media.TextAlignment.Right;
                    tb.Margin = new Thickness(0, 0, 10, 0);
                    break;

                case 9: // Current Statement — monospace, dimmer
                    tb.FontFamily = new FontFamily("Consolas, Menlo, Monaco, monospace");
                    tb.Foreground = GetBrush("TextSecondary") ?? tb.Foreground;
                    tb.Margin = new Thickness(6, 0, 0, 0);
                    break;
            }
        }
    }

    private static IBrush? GetBrush(string key)
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true && res is IBrush b)
            return b;
        return null;
    }

    private void OnJobRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is JobRow job)
        {
            if (job.LastRunOutcome == "Failed")
                e.Row.Background = new SolidColorBrush(Color.FromArgb(25, 255, 80, 80));
            else if (job.IsExecuting)
                e.Row.Background = new SolidColorBrush(Color.FromArgb(25, 80, 150, 255));
            else
                e.Row.Background = Brushes.Transparent;

            e.Row.Opacity = job.IsEnabled ? 1.0 : 0.5;
        }
    }

    private void OnJobHistoryRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is JobHistoryRow row)
        {
            e.Row.Background = row.RunStatus switch
            {
                0 => new SolidColorBrush(Color.FromArgb(25, 255, 80, 80)),   // Failed
                1 => new SolidColorBrush(Color.FromArgb(15, 80, 200, 80)),   // Succeeded
                3 => new SolidColorBrush(Color.FromArgb(25, 255, 200, 50)),  // Cancelled
                _ => Brushes.Transparent
            };
        }
    }

    // ── Misc Events ───────────────────────────────────────────────

    private void OnSessionDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        _viewModel?.OpenQueryInEditor(_viewModel.SelectedSession);
    }

    private async Task CopySessionQueryAsync()
    {
        if (_viewModel?.SelectedSession == null) return;
        var text = !string.IsNullOrWhiteSpace(_viewModel.SelectedSession.QueryText)
            ? _viewModel.SelectedSession.QueryText
            : _viewModel.SelectedSession.CurrentStatement;

        if (string.IsNullOrWhiteSpace(text)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(text);
    }
}
