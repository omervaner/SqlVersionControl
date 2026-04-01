using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost
{
    // ── Save / Open Public API (for MainWindow) ──────────────────────

    /// <summary>
    /// Save active query. If no path yet, shows SaveQueryDialog first.
    /// Returns true if saved successfully.
    /// </summary>
    public async Task<bool> SaveActiveQueryAsync(QueryFileService svc, SettingsService settings)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm == null) return false;

        // Sync text from editor
        vm.SqlText = tab.Editor.Text;

        if (vm.CurrentQueryPath != null)
        {
            vm.Save(svc, settings);
            RebuildTabStrip();
            return true;
        }

        // No path yet — show Save As dialog
        return await SaveAsActiveQueryAsync(svc, settings);
    }

    /// <summary>
    /// Always shows native Save File dialog, then saves.
    /// </summary>
    public async Task<bool> SaveAsActiveQueryAsync(QueryFileService svc, SettingsService settings)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return false;
        var tab = _tabs[_activeTabIndex];
        var vm = tab.DataContext as QueryTabViewModel;
        if (vm == null) return false;

        // Sync text from editor
        vm.SqlText = tab.Editor.Text;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return false;

        var defaultName = vm.CurrentQueryName ?? "query";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save Query",
            SuggestedFileName = defaultName,
            DefaultExtension = "sql",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("SQL Files") { Patterns = ["*.sql"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (file == null) return false;

        var path = file.TryGetLocalPath();
        if (path == null) return false;

        vm.CurrentQueryPath = path;
        vm.CurrentQueryName = Path.GetFileNameWithoutExtension(path);
        vm.Save(svc, settings);
        RebuildTabStrip();
        return true;
    }

    /// <summary>
    /// Shows native Open File dialog, creates a new tab and loads the selected file.
    /// </summary>
    public async Task OpenQueryAsync(QueryFileService svc, SettingsService settings)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open SQL File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("SQL Files") { Patterns = ["*.sql"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path == null) return;

        OpenQueryFromPath(path, svc, settings);
    }

    /// <summary>
    /// Open a query file directly (for Recent Files menu).
    /// </summary>
    public void OpenQueryFromPath(string path, QueryFileService svc, SettingsService settings)
    {
        if (!File.Exists(path)) return;

        AddNewTab();
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            var vm = tab.DataContext as QueryTabViewModel;
            if (vm != null)
            {
                vm.LoadFromFile(path, svc, settings);
                // Update editor text to match loaded SQL
                tab.Editor.Text = vm.SqlText;
                RebuildTabStrip();
            }
        }
    }
}
