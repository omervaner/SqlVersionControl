using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost
{
    // ── Session Save / Restore ─────────────────────────────────────

    /// <summary>
    /// Save current tab state to session file. Called on tab create, close, switch, and app close.
    /// </summary>
    public void SaveSession()
    {
        if (_sessionService == null) return;

        var tabs = new List<TabState>();
        foreach (var tabView in _tabs)
        {
            if (tabView.DataContext is not QueryTabViewModel vm) continue;

            // Sync latest editor text
            vm.SqlText = tabView.Editor.Text;

            tabs.Add(new TabState
            {
                SqlText = vm.SqlText,
                SelectedDatabase = vm.SelectedDatabase,
                SavedPath = vm.CurrentQueryPath,
                QueryName = vm.CurrentQueryName,
                CursorPosition = tabView.Editor.CaretOffset,
                // Per-tab connection
                ConnectionServer = vm.TabConnectionProfile?.Server,
                ConnectionDatabase = vm.TabConnectionProfile?.Database,
                ConnectionUsername = vm.TabConnectionProfile?.Username,
                ConnectionUseWindowsAuth = vm.TabConnectionProfile?.UseWindowsAuth,
                ConnectionProfileName = vm.TabConnectionProfile?.Name,
                ConnectionProfileColor = vm.TabConnectionProfile?.Color,
                // Registry connection ID (v2.2.0)
                ConnectionId = vm.TabConnectionProfile?.Id,
            });
        }

        _sessionService.SaveTabs(tabs, _activeTabIndex);
    }

    private void RestoreSession()
    {
        if (_sessionService == null)
        {
            AddNewTab();
            return;
        }

        var data = _sessionService.Data;
        if (data.Tabs.Count == 0)
        {
            AddNewTab();
            return;
        }

        _restoringSession = true;
        var disconnectedTabs = new List<string>();
        try
        {
            foreach (var tabState in data.Tabs)
            {
                AddNewTab();
                if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) continue;

                var tabView = _tabs[_activeTabIndex];
                var vm = tabView.DataContext as QueryTabViewModel;
                if (vm == null) continue;

                // Restore saved query metadata
                if (tabState.SavedPath != null)
                {
                    vm.CurrentQueryPath = tabState.SavedPath;
                    vm.CurrentQueryName = tabState.QueryName;
                    if (tabState.QueryName != null)
                        vm.TabTitle = tabState.QueryName;
                }

                // Restore per-tab connection — try registry first, fall back to legacy
                var restored = false;
                var hasConnection = false;
                if (_registry != null && tabState.ConnectionId != null)
                {
                    var managed = _registry.GetById(tabState.ConnectionId);
                    if (managed != null)
                    {
                        vm.TabConnectionProfile = managed.Config;
                        if (managed.ResolvedConnectionString != null)
                        {
                            vm.TabConnectionString = managed.ResolvedConnectionString;
                            _ = LoadDatabasesForTabAsync(vm, vm.TabConnectionString, tabState.SelectedDatabase);
                            hasConnection = true;
                        }
                        restored = true;
                    }
                    else if (tabState.ConnectionServer != null)
                    {
                        // ConnectionId not found — try matching by server/database/username
                        var match = _registry.FindByServerAndDatabase(
                            tabState.ConnectionServer,
                            tabState.ConnectionDatabase ?? "",
                            tabState.ConnectionUsername);
                        if (match != null)
                        {
                            vm.TabConnectionProfile = match.Config;
                            if (match.ResolvedConnectionString != null)
                            {
                                vm.TabConnectionString = match.ResolvedConnectionString;
                                _ = LoadDatabasesForTabAsync(vm, vm.TabConnectionString, tabState.SelectedDatabase);
                                hasConnection = true;
                            }
                            restored = true;
                        }
                    }
                }

                // Legacy fallback: build connection string from saved fields
                if (!restored && tabState.ConnectionServer != null)
                {
                    var profile = new SavedConnection
                    {
                        Server = tabState.ConnectionServer,
                        Database = tabState.ConnectionDatabase ?? "",
                        Username = tabState.ConnectionUsername ?? "",
                        UseWindowsAuth = tabState.ConnectionUseWindowsAuth ?? false,
                        Name = tabState.ConnectionProfileName,
                        Color = tabState.ConnectionProfileColor,
                    };
                    vm.TabConnectionProfile = profile;

                    // Rebuild connection string
                    var connSettings = new ConnectionSettings
                    {
                        Server = profile.Server,
                        Database = profile.Database,
                        UseWindowsAuth = profile.UseWindowsAuth,
                        Username = profile.Username,
                        Password = profile.UseWindowsAuth ? ""
                            : PasswordStore.Get(profile.Server, profile.Database, profile.Username) ?? ""
                    };
                    vm.TabConnectionString = connSettings.ConnectionString;

                    // Load databases for this server async
                    _ = LoadDatabasesForTabAsync(vm, vm.TabConnectionString, tabState.SelectedDatabase);
                    hasConnection = true;
                }

                // Track tabs that couldn't reconnect
                if (!hasConnection && tabState.ConnectionServer != null)
                {
                    var tabName = tabState.QueryName ?? vm.TabTitle ?? "Query";
                    var connName = tabState.ConnectionProfileName ?? tabState.ConnectionServer;
                    disconnectedTabs.Add($"{tabName} ({connName})");
                }

                // Restore editor text
                tabView.Editor.Text = tabState.SqlText;
                vm.SetInitialText(tabState.SqlText);

                // Restore cursor position
                if (tabState.CursorPosition >= 0 && tabState.CursorPosition <= tabState.SqlText.Length)
                    tabView.Editor.CaretOffset = tabState.CursorPosition;

                RebuildTabStrip();
            }

            // Restore active tab
            var targetIndex = Math.Clamp(data.ActiveTabIndex, 0, _tabs.Count - 1);
            SwitchToTab(targetIndex);
        }
        finally
        {
            _restoringSession = false;
        }

        // Notify user about tabs that couldn't reconnect
        if (disconnectedTabs.Count > 0)
        {
            var count = disconnectedTabs.Count;
            var msg = count == 1
                ? $"1 tab could not reconnect: {disconnectedTabs[0]}"
                : $"{count} tabs could not reconnect — their saved connections are unavailable. Use the ↻ button on each tab to retry.";
            SessionRestoreWarning?.Invoke(msg);
        }
    }

    // ── Autocomplete Toggle ────────────────────────────────────────

    private void OnAutocompleteToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = GetSettingsService();
        if (settings == null) return;

        settings.Settings.AutocompleteEnabled = !settings.Settings.AutocompleteEnabled;
        settings.Save();
        UpdateAutocompleteToggleVisual();
    }

    private void UpdateAutocompleteToggleVisual()
    {
        var enabled = GetSettingsService()?.Settings.AutocompleteEnabled ?? true;

        // Active: accent foreground on transparent bg; Inactive: muted foreground
        var iconBrush = enabled ? FindBrush("ButtonToggleActive") : FindBrush("TextDisabled");
        AutocompleteToggleButton.Background = Avalonia.Media.Brushes.Transparent;
        AutocompleteToggleButton.Foreground = iconBrush;
        AutocompleteToggleButton.BorderThickness = new Thickness(0);
        // Update the PathIcon foreground inside the button
        if (AutocompleteToggleButton.Content is Avalonia.Controls.PathIcon pathIcon)
            pathIcon.Foreground = iconBrush;
    }

    /// <summary>Whether autocomplete is currently enabled (checked by QueryTabView).</summary>
    public bool IsAutocompleteEnabled => GetSettingsService()?.Settings.AutocompleteEnabled ?? true;

    // ── Query History Panel ─────────────────────────────────────────

    private void ToggleHistoryPanel()
    {
        var visible = !HistoryPanel.IsVisible;
        HistoryPanel.IsVisible = visible;
        HistorySplitter.IsVisible = visible;
        if (visible) RefreshHistoryGrid();
    }

    private void RefreshHistoryGrid()
    {
        if (_sessionService == null) return;

        var history = _sessionService.GetQueryHistory();
        var filter = HistorySearchBox.Text?.Trim() ?? "";

        var items = history
            .Where(h => string.IsNullOrEmpty(filter)
                || h.SqlText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (h.Database?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
            .Select(h => new HistoryDisplayItem(h))
            .ToList();

        HistoryGrid.ItemsSource = items;
    }

    private void ClearQueryHistory()
    {
        if (_sessionService == null) return;
        _sessionService.ClearQueryHistory();
        RefreshHistoryGrid();
    }

    private void OnHistoryGridDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryDisplayItem item) return;

        AddNewTab();
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            var vm = tab.DataContext as QueryTabViewModel;
            if (vm != null)
            {
                tab.Editor.Text = item.Entry.SqlText;
                vm.SetInitialText(item.Entry.SqlText);
                if (item.Entry.Database != null && vm.Databases.Contains(item.Entry.Database))
                    vm.SelectedDatabase = item.Entry.Database;
            }
        }
    }

    private static string FormatTimeAgo(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return when.ToString("MMM d");
    }
}
