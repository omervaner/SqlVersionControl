using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class MainWindow
{
    private List<CommandPaletteItem>? _allCommands;

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
        // Ctrl+K (comment), Ctrl+Shift+K (uncomment), Ctrl+L (exec plan),
        // Ctrl+Shift+U (upper), Ctrl+Shift+L (lower), Alt+Z (word wrap)
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (QueryEditorTab.IsChecked == true &&
            ((alt && (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Z)) ||
             (ctrl && e.Key == Key.G) ||
             (ctrl && (e.Key == Key.K || e.Key == Key.L)) ||
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
                ActivityTab.IsChecked = true;
                e.Handled = true;
                break;

            case Key.D5:
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
                if (QueryEditorTab.IsChecked == true)
                {
                    var qeHost = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
                    qeHost?.ToggleActiveResultsPanel();
                }
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

    // ── Command Palette ─────────────────────────────────────────────

    private List<CommandPaletteItem> BuildCommandRegistry()
    {
        var host = this.FindControl<QueryEditorHost>("QueryEditorHostControl");
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
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
            new() { Name = "Uncomment Lines", Shortcut = $"{mod}+Shift+K", Description = "Remove line comments", Execute = () => GetActiveQueryTabView()?.UncommentLines() },
            new() { Name = "Uppercase Selection", Shortcut = $"{mod}+Shift+U", Description = "Transform to uppercase", Execute = () => GetActiveQueryTabView()?.UppercaseSelection() },
            new() { Name = "Lowercase Selection", Shortcut = $"{mod}+Shift+L", Description = "Transform to lowercase", Execute = () => GetActiveQueryTabView()?.LowercaseSelection() },
            new() { Name = "Toggle Word Wrap", Shortcut = "Alt+Z", Description = "Wrap long lines", Execute = () => { var e = GetActiveEditor(); if (e != null) e.WordWrap = !e.WordWrap; } },

            // View
            new() { Name = "Toggle Object Explorer", Shortcut = $"{mod}+B", Description = "Show/hide sidebar", Execute = () => host?.ToggleObjectExplorer() },
            new() { Name = "Toggle Results Panel", Shortcut = $"{mod}+R", Description = "Show/hide results", Execute = () => host?.ToggleActiveResultsPanel() },
            new() { Name = "Zoom In", Shortcut = $"{mod}+=", Description = "Increase font size", Execute = () => { var s = Math.Min(_settings.Settings.FontSize + 1, 32); _settings.Settings.FontSize = s; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, s); _settings.Save(); } },
            new() { Name = "Zoom Out", Shortcut = $"{mod}+-", Description = "Decrease font size", Execute = () => { var s = Math.Max(_settings.Settings.FontSize - 1, 8); _settings.Settings.FontSize = s; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, s); _settings.Save(); } },
            new() { Name = "Reset Zoom", Shortcut = "", Description = "Reset to default font size", Execute = () => { _settings.Settings.FontSize = 12; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, 12); _settings.Save(); } },
            new() { Name = "Toggle Dark/Light Theme", Shortcut = $"{mod}+Shift+T", Description = "Switch theme", Execute = () => { _settings.Settings.UseDarkTheme = !_settings.Settings.UseDarkTheme; ThemeManager.ApplyTheme(_settings.Settings.UseDarkTheme, _settings.Settings.FontSize); _settings.Save(); } },

            // Tools
            new() { Name = "Format SQL", Shortcut = "Ctrl+Shift+F", Description = "Format selected SQL", Execute = () => host?.FormatSqlInEditor() },
            new() { Name = "Quick Quote Selection", Shortcut = "Ctrl+Shift+Q", Description = "Quote selected text", Execute = () => host?.QuickQuoteSelection(false) },
            new() { Name = "SQL Quoter", Shortcut = "", Description = "Open SQL Quoter dialog", Execute = () => _ = ShowSqlQuoterDialogAsync() },
            new() { Name = "Text Compare", Shortcut = "", Description = "Compare two text blocks", Execute = () => _ = new TextCompareDialog().ShowDialogDetached(this) },
            new() { Name = "Index Analysis", Shortcut = "", Description = "Analyze unused/missing indexes", Execute = () => _ = ShowIndexAnalysisDialogAsync() },

            // Tabs
            new() { Name = "Close Tab", Shortcut = $"{mod}+W", Description = "Close active query tab", Execute = () => { if (host != null) _ = host.CloseActiveTabAsync(); } },

            // Navigation
            new() { Name = "Query Editor", Shortcut = $"{mod}+1", Description = "Switch to editor", Execute = () => QueryEditorTab.IsChecked = true },
            new() { Name = "Version History", Shortcut = $"{mod}+2", Description = "Switch to history", Execute = () => VersionHistoryTab.IsChecked = true },
            new() { Name = "Compare Databases", Shortcut = $"{mod}+3", Description = "Switch to compare", Execute = () => CompareTab.IsChecked = true },
            new() { Name = "Activity Monitor", Shortcut = $"{mod}+4", Description = "Switch to activity", Execute = () => ActivityTab.IsChecked = true },
            new() { Name = "Query Trace", Shortcut = $"{mod}+5", Description = "Switch to trace", Execute = () => TraceTab.IsChecked = true },

            // Run
            new() { Name = "Run Query", Shortcut = "F5", Description = "Execute query", Execute = () => host?.RunActiveQuery() },
            new() { Name = "Run with Trace", Shortcut = "Ctrl+Shift+F5", Description = "Execute with XE tracing", Execute = () => host?.RunActiveWithTrace() },
            new() { Name = "Estimated Execution Plan", Shortcut = $"{mod}+L", Description = "Show estimated plan for current SQL", Execute = () => { if (host != null) _ = host.GenerateExecPlanForActiveTab(); } },

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
}
