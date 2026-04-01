using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost
{
    // ── Object Explorer Collapse ────────────────────────────────────

    public void ToggleObjectExplorer()
    {
        try
        {
            var colDefs = MainGrid.ColumnDefinitions;
            if (_oeCollapsed)
            {
                // Expand — restore saved width
                var w = _settings?.Settings.ObjectExplorerWidth ?? 220;
                if (w <= 0 || double.IsNaN(w) || double.IsInfinity(w)) w = 220;
                colDefs[0].Width = new GridLength(w, GridUnitType.Pixel);
                OeSplitter.IsEnabled = true;
                ObjectExplorerPanel.IsVisible = true;
                OeExpandButton.IsVisible = false;
                _oeCollapsed = false;
            }
            else
            {
                // Save current width before collapsing
                var currentWidth = colDefs[0].ActualWidth;
                if (currentWidth > 30 && !double.IsNaN(currentWidth) && !double.IsInfinity(currentWidth) && _settings != null)
                {
                    _settings.Settings.ObjectExplorerWidth = currentWidth;
                }
                colDefs[0].Width = new GridLength(14, GridUnitType.Pixel);
                OeSplitter.IsEnabled = false;
                ObjectExplorerPanel.IsVisible = false;
                OeExpandButton.IsVisible = true;
                _oeCollapsed = true;
            }

            if (_settings != null)
            {
                _settings.Settings.ObjectExplorerCollapsed = _oeCollapsed;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryEditorHost.ToggleObjectExplorer", ex);
        }
    }

    private void RestoreObjectExplorerState()
    {
        try
        {
            if (_settings == null) return;
            var s = _settings.Settings;

            // Validate saved width
            if (s.ObjectExplorerWidth <= 0 || double.IsNaN(s.ObjectExplorerWidth) || double.IsInfinity(s.ObjectExplorerWidth))
                s.ObjectExplorerWidth = 220;

            // Restore width
            var w = s.ObjectExplorerWidth > 30 ? s.ObjectExplorerWidth : 220;
            MainGrid.ColumnDefinitions[0].Width = new GridLength(w, GridUnitType.Pixel);

            // Restore collapsed state
            if (s.ObjectExplorerCollapsed)
            {
                MainGrid.ColumnDefinitions[0].Width = new GridLength(14, GridUnitType.Pixel);
                OeSplitter.IsEnabled = false;
                ObjectExplorerPanel.IsVisible = false;
                OeExpandButton.IsVisible = true;
                _oeCollapsed = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryEditorHost.RestoreObjectExplorerState", ex);
        }
    }

    // ── Intellisense Schema Cache ─────────────────────────────────────

    private static bool IsDdlStatement(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnTabDatabaseChanged(QueryTabViewModel tabVm)
    {
        if (_db == null || string.IsNullOrEmpty(tabVm.SelectedDatabase)) return;

        var connStr = tabVm.TabConnectionString ?? _primaryConnectionString;
        if (connStr == null) return;

        var cacheKey = $"{connStr}|{tabVm.SelectedDatabase}";

        if (!_intellisenseCache.TryGetValue(cacheKey, out var service))
        {
            service = new IntellisenseService();
            _intellisenseCache[cacheKey] = service;

            try
            {
                var effectiveConn = tabVm.GetEffectiveConnectionString(tabVm.SelectedDatabase);
                var tables = await _db.GetTablesAsync(effectiveConn, tabVm.SelectedDatabase);
                var views = await _db.GetViewsAsync(effectiveConn, tabVm.SelectedDatabase);
                var columns = await _db.GetAllColumnsAsync(effectiveConn, tabVm.SelectedDatabase);
                service.SetSchema(tables, views, columns);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Schema loading failed for {tabVm.SelectedDatabase}: {ex.Message}");
            }
        }

        var tabIndex = FindTabIndex(tabVm);
        if (tabIndex >= 0 && tabIndex < _tabs.Count)
            _tabs[tabIndex].SetIntellisenseService(service);
    }

    private int FindTabIndex(QueryTabViewModel vm)
    {
        for (int i = 0; i < _tabs.Count; i++)
            if (_tabs[i].DataContext == vm) return i;
        return -1;
    }

    // ── Public API (for MainWindow) ──────────────────────────────────

    /// <summary>
    /// Handle F5 / Ctrl+Enter by delegating to active tab.
    /// </summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        return _tabs[_activeTabIndex].HandleKeyDown(e);
    }

    /// <summary>
    /// Run the query in the active tab.
    /// </summary>
    public void RunActiveQuery()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm != null)
        {
            vm.SelectedSqlText = tab.Editor.SelectedText ?? "";
            vm.SqlText = tab.Editor.Text;
            if (vm.RunQueryCommand.CanExecute(null))
                _ = vm.RunQueryCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Run the query in the active tab with Extended Events tracing.
    /// </summary>
    public void RunActiveWithTrace()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm != null)
        {
            vm.SelectedSqlText = tab.Editor.SelectedText ?? "";
            vm.SqlText = tab.Editor.Text;
            if (vm.RunWithTraceCommand.CanExecute(null))
                _ = vm.RunWithTraceCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Stop the query in the active tab.
    /// </summary>
    public void StopActiveQuery()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var vm = _tabs[_activeTabIndex].DataContext as QueryTabViewModel;
        if (vm?.StopQueryCommand.CanExecute(null) == true)
            vm.StopQueryCommand.Execute(null);
    }

    /// <summary>
    /// Get the active tab's TextEditor (for Edit menu pass-through).
    /// </summary>
    public TextEditor? GetActiveEditor()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return null;
        return _tabs[_activeTabIndex].Editor;
    }

    /// <summary>Get the active QueryTabView (for editor operations that need the full view).</summary>
    public QueryTabView? GetActiveTabView()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return null;
        return _tabs[_activeTabIndex];
    }

    /// <summary>
    /// Replace the editor's selected text with quoted, comma-separated values.
    /// Uses shared SqlQuoterService logic.
    /// </summary>
    public void QuickQuoteSelection(bool nPrefix)
    {
        var editor = GetActiveEditor();
        if (editor == null) return;

        var selected = editor.SelectedText;
        if (string.IsNullOrWhiteSpace(selected)) return;

        var quoted = SqlQuoterService.QuickQuote(selected, nPrefix);
        if (quoted.Length == 0) return;

        editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, quoted);
    }

    /// <summary>
    /// Format SQL in the active editor (selected text or all).
    /// </summary>
    public void FormatSqlInEditor()
    {
        var editor = GetActiveEditor();
        if (editor == null) return;

        if (editor.SelectionLength > 0)
        {
            var formatted = SqlFormatterService.Format(editor.SelectedText);
            editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, formatted);
        }
        else
        {
            var caret = editor.CaretOffset;
            var formatted = SqlFormatterService.Format(editor.Text);
            editor.Text = formatted;
            if (caret <= formatted.Length)
                editor.CaretOffset = caret;
        }
    }

    public void ToggleActiveResultsPanel()
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].ToggleResultsPanel();
    }

    public void FocusActiveDatabasePicker()
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].FocusDatabasePicker();
    }

    /// <summary>
    /// Reload databases into Object Explorer and all tabs.
    /// In multi-connection mode, populates OE from registry connections.
    /// In single-connection mode, loads databases as root nodes (legacy).
    /// </summary>
    public async Task ReloadDatabasesAsync()
    {
        if (_db == null || _viewModel == null) return;

        try
        {
            // Multi-connection mode: populate from registry
            if (_registry != null && _registry.ActiveConnections.Any())
            {
                _viewModel.ObjectExplorer.LoadFromRegistry();

                // Update tabs with databases from their connection
                foreach (var tab in _tabs)
                {
                    if (tab.DataContext is QueryTabViewModel vm && vm.TabConnectionString != null)
                        _ = LoadDatabasesForTabAsync(vm, vm.TabConnectionString);
                }
                return;
            }

            // Legacy single-connection mode
            var dbs = await _db.GetDatabasesAsync();
            _cachedDatabases = new List<string>(dbs);

            // Update Object Explorer
            await _viewModel.ObjectExplorer.LoadDatabasesAsync(dbs);

            // Update all tabs
            foreach (var tab in _tabs)
            {
                if (tab.DataContext is QueryTabViewModel vm)
                    vm.SetDatabases(dbs, vm.SelectedDatabase);
            }
        }
        catch
        {
            // Connection might not be ready yet
        }
    }

    private async Task LoadDatabasesForTabAsync(QueryTabViewModel vm, string connectionString, string? selectDatabase = null)
    {
        try
        {
            List<string> dbs;
            if (_serverCache.TryGetValue(connectionString, out var cached) && cached.Databases.Count > 0)
            {
                dbs = cached.Databases;
            }
            else
            {
                dbs = await _db!.GetDatabasesAsync(connectionString);
                if (!_serverCache.ContainsKey(connectionString))
                    _serverCache[connectionString] = new CachedServerData();
                _serverCache[connectionString].Databases = dbs;
            }
            vm.SetDatabases(dbs, selectDatabase ?? vm.SelectedDatabase);
        }
        catch
        {
            // Server might not be reachable — tab will show empty DB list
        }
    }

    private SettingsService? GetSettingsService()
    {
        var mainWindow = TopLevel.GetTopLevel(this) as MainWindow;
        return mainWindow?.AppSettings;
    }
}
