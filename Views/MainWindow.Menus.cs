using Avalonia.Controls;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class MainWindow
{
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
        MenuTextCompare.Click += async (_, _) => await new TextCompareDialog().ShowDialogDetached(this);
        MenuIndexAnalysis.Click += async (_, _) => await ShowIndexAnalysisDialogAsync();

        // Help menu
        MenuViewLog.Click += (_, _) => OpenLogInNewTab();
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
}
