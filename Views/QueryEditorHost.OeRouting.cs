using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using SqlVersionControl.Models;
using SqlVersionControl.Services;
using SqlVersionControl.ViewModels;

namespace SqlVersionControl.Views;

public partial class QueryEditorHost
{
    // ── Object Explorer Event Routing ────────────────────────────────

    /// <summary>Open a script in a new tab (for tools/dialogs that generate SQL).</summary>
    public void OpenScriptInNewTab(string sql, string? connectionString = null, SavedConnection? profile = null)
    {
        var activeVm = ActiveTabViewModel;
        AddNewTab(connectionString ?? activeVm?.TabConnectionString, profile ?? activeVm?.TabConnectionProfile);
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertText(sql, false);
    }

    /// <summary>Open a .sql file dropped onto the editor in a new tab.</summary>
    private void OpenDroppedFile(string path)
    {
        try
        {
            var sql = System.IO.File.ReadAllText(path);
            AddNewTab();
            if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            {
                var tab = _tabs[_activeTabIndex];
                var vm = tab.DataContext as QueryTabViewModel;
                if (vm != null)
                {
                    vm.TabTitle = System.IO.Path.GetFileName(path);
                    tab.InsertText(sql, false);
                }
                RebuildTabStrip();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("QueryEditorHost.OpenDroppedFile", ex);
        }
    }

    /// <summary>Resolve connection from OE node, falling back to active tab.</summary>
    private (string? connStr, SavedConnection? profile) ResolveOeConnection(string? connectionId)
    {
        string? connStr = null;
        SavedConnection? profile = null;

        if (connectionId != null && _registry != null)
        {
            var managed = _registry.GetById(connectionId);
            if (managed != null)
            {
                connStr = managed.ResolvedConnectionString;
                profile = managed.Config;
            }
        }

        connStr ??= ActiveTabViewModel?.TabConnectionString;
        profile ??= ActiveTabViewModel?.TabConnectionProfile;
        return (connStr, profile);
    }

    private void OnInsertText(string sql, bool autoRun, string? databaseName = null, string? connectionId = null)
    {
        var (connStr, profile) = ResolveOeConnection(connectionId);
        AddNewTab(connStr, profile);

        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            if (databaseName != null && connStr != null)
                _ = LoadDatabasesForTabAsync(
                    (_tabs[_activeTabIndex].DataContext as QueryTabViewModel)!, connStr, databaseName);

            _tabs[_activeTabIndex].InsertText(sql, autoRun);
        }
    }

    private void OnInsertAtCursor(string text)
    {
        // Column insert stays in current tab (user is building a query)
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertAtCursor(text);
    }

    private async void OnCopyToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task<string?> OnPeekDefinitionRequested(string objectName)
    {
        if (_db == null) return null;

        var activeVm = ActiveTabViewModel;
        var connStr = activeVm?.TabConnectionString ?? _primaryConnectionString;
        var database = activeVm?.SelectedDatabase;
        if (connStr == null || database == null) return null;

        var (schema, name) = Helpers.SqlNameParser.ParseSchemaQualifiedName(objectName);

        // Try exact match first
        var definition = await _db.GetObjectDefinitionAsync(connStr, database, schema, name);
        if (definition != null) return definition;

        // If no dot was provided, try all schemas by searching with just the name
        if (!objectName.Contains('.'))
        {
            // Try common schemas
            foreach (var s in new[] { "dbo", "sys" })
            {
                definition = await _db.GetObjectDefinitionAsync(connStr, database, s, name);
                if (definition != null) return definition;
            }
        }

        return null;
    }

    private async Task OnQuickExecuteRequested(string objectName)
    {
        if (_db == null) return;

        var activeVm = ActiveTabViewModel;
        var connStr = activeVm?.TabConnectionString ?? _primaryConnectionString;
        var database = activeVm?.SelectedDatabase;
        if (connStr == null || database == null) return;

        var (schema, name) = Helpers.SqlNameParser.ParseSchemaQualifiedName(objectName);

        // Fetch parameters
        var parameters = await _db.GetProcParametersDetailedAsync(connStr, database, schema, name);

        // If object not found, try without schema
        if (parameters.Count == 0 && !objectName.Contains('.'))
        {
            // Verify the proc actually exists by checking definition
            var def = await _db.GetObjectDefinitionAsync(connStr, database, schema, name);
            if (def == null) return; // Object not found
        }

        // Build the template
        var sb = new System.Text.StringBuilder();

        foreach (var p in parameters)
        {
            var paramName = p.Name.TrimStart('@');
            var typeFmt = Services.SqlTypeFormatter.Format(p.TypeName, p.MaxLength, p.Precision, p.Scale);
            sb.Append($"DECLARE @{paramName} {typeFmt} = NULL");
            if (p.IsOutput) sb.Append("  -- OUTPUT");
            sb.AppendLine();
        }

        if (parameters.Count > 0) sb.AppendLine();

        sb.Append($"EXEC [{schema}].[{name}]");
        if (parameters.Count > 0)
        {
            sb.AppendLine();
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var paramName = p.Name.TrimStart('@');
                var comma = i < parameters.Count - 1 ? "," : "";
                var output = p.IsOutput ? " OUTPUT" : "";
                sb.AppendLine($"    @{paramName} = @{paramName}{output}{comma}");
            }
        }

        // Open in new tab with proc name as title
        var template = sb.ToString();
        AddNewTab(connStr, activeVm?.TabConnectionProfile);
        var newTab = ActiveTabViewModel;
        if (newTab != null)
        {
            newTab.SelectedDatabase = database;
            newTab.CurrentQueryName = name;
            newTab.TabTitle = name;
        }
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            _tabs[_activeTabIndex].InsertText(template, false);
    }

    private void OnShowDependenciesFromEditor(string objectName)
    {
        var activeVm = ActiveTabViewModel;
        var database = activeVm?.SelectedDatabase;
        if (database == null) return;

        var (schema, name) = Helpers.SqlNameParser.ParseSchemaQualifiedName(objectName);

        // Create a temporary node for the dependency lookup
        var node = new ObjectExplorerNode
        {
            Name = name,
            Schema = schema,
            DatabaseName = database,
            NodeType = ObjectExplorerNodeType.Proc, // type doesn't matter for dependencies
            ConnectionId = activeVm?.TabConnectionProfile?.Id
        };

        _ = _viewModel.ObjectExplorer.ShowDependenciesAsync(node);
    }

    private void OnProcDropRequested(ObjectExplorerNode node)
    {
        if (_viewModel == null) return;
        // ViewDefinitionAsync fires InsertTextRequested → OnInsertText → new tab
        _ = _viewModel.ObjectExplorer.ViewDefinitionAsync(node);
    }

    private void OnEditDataRequested(string sql, string? databaseName = null, string? connectionId = null)
    {
        var (connStr, profile) = ResolveOeConnection(connectionId);
        AddNewTab(connStr, profile);
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            var vm = tab.DataContext as QueryTabViewModel;
            if (vm != null)
            {
                vm.AutoEnterEditMode = true;
                if (databaseName != null && connStr != null)
                    _ = LoadDatabasesForTabAsync(vm, connStr, databaseName);
            }
            tab.InsertText(sql, autoRun: true);
        }
    }

    private async void OnAlterSequenceRequested(ObjectExplorerNode node)
    {
        if (_db == null) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent == null) return;

        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var currentValue = ParseCurrentValue(node.TypeInfo);

        var dialog = new AlterSequenceDialog($"[{schema}].[{node.Name}]", currentValue);
        await dialog.ShowDialog(parent);

        if (dialog.NewValue == null) return;

        var confirm = new ConfirmDialog($"Reset sequence {schema}.{node.Name} to {dialog.NewValue:N0}?");
        await confirm.ShowDialog(parent);

        if (!confirm.Confirmed) return;

        try
        {
            var connStr = ActiveTabViewModel?.TabConnectionString ?? _primaryConnectionString;
            if (connStr == null) return;
            await _db.AlterSequenceRestartAsync(connStr, node.DatabaseName, schema, node.Name, dialog.NewValue.Value);

            // Update the TypeInfo in-place so the tree reflects the new value
            var dataType = node.TypeInfo.Split(',')[0].Trim();
            node.TypeInfo = $"{dataType}, Current: {dialog.NewValue.Value}";
        }
        catch (Exception ex)
        {
            var errDialog = new ConfirmDialog($"Error: {ex.Message}");
            await errDialog.ShowDialog(parent);
        }
    }

    private async void OnResetSequenceRequested(ObjectExplorerNode node)
    {
        if (_db == null) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent == null) return;

        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;

        var confirm = new ConfirmDialog($"Reset sequence {schema}.{node.Name} to 0?");
        await confirm.ShowDialog(parent);

        if (!confirm.Confirmed) return;

        try
        {
            var connStr = ActiveTabViewModel?.TabConnectionString ?? _primaryConnectionString;
            if (connStr == null) return;
            await _db.AlterSequenceRestartAsync(connStr, node.DatabaseName, schema, node.Name, 0);

            var dataType = node.TypeInfo.Split(',')[0].Trim();
            node.TypeInfo = $"{dataType}, Current: 0";
        }
        catch (Exception ex)
        {
            var errDialog = new ConfirmDialog($"Error: {ex.Message}");
            await errDialog.ShowDialog(parent);
        }
    }

    private static long ParseCurrentValue(string typeInfo)
    {
        // TypeInfo format: "BIGINT, Current: 45231"
        var idx = typeInfo.IndexOf("Current:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && long.TryParse(typeInfo[(idx + 8)..].Trim(), out var val))
            return val;
        return 0;
    }

    // ── SQL Agent Jobs ─────────────────────────────────────────────────

    private async void OnStartJobRequested(ObjectExplorerNode node)
    {
        if (_db == null) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent == null) return;

        var confirm = new ConfirmDialog($"Start job '{node.Name}'?");
        await confirm.ShowDialog(parent);

        if (!confirm.Confirmed) return;

        try
        {
            var connStr = ActiveTabViewModel?.TabConnectionString ?? _primaryConnectionString;
            if (connStr == null) return;
            await _db.StartJobAsync(connStr, node.Name);

            node.TypeInfo = "Enabled, Last: Running";
        }
        catch (Exception ex)
        {
            var errDialog = new ConfirmDialog($"Error: {ex.Message}");
            await errDialog.ShowDialog(parent);
        }
    }

    private void RefreshParentJobsFolder(ObjectExplorerNode jobNode)
    {
        if (_viewModel == null) return;
        // Find the parent "Jobs" folder and refresh it
        foreach (var db in _viewModel.ObjectExplorer.RootNodes)
        {
            foreach (var folder in db.Children)
            {
                if (folder.Name == "Jobs" && folder.Children.Contains(jobNode))
                {
                    _viewModel.ObjectExplorer.RefreshNode(folder);
                    return;
                }
            }
        }
    }

    // ── Drag-and-Drop ─────────────────────────────────────────────────

    private Point _dragStartPoint;
    private bool _dragPending;
    private ObjectExplorerNode? _dragNode;
    private const double DragThreshold = 8;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ObjectExplorerTree).Properties.IsLeftButtonPressed &&
            e.Source is Visual visual)
        {
            var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
            if (treeViewItem?.DataContext is ObjectExplorerNode node &&
                node.NodeType is ObjectExplorerNodeType.Table or ObjectExplorerNodeType.View
                    or ObjectExplorerNodeType.Proc or ObjectExplorerNodeType.Function
                    or ObjectExplorerNodeType.Column)
            {
                _dragStartPoint = e.GetPosition(ObjectExplorerTree);
                _dragNode = node;
                _dragPending = true;
                return;
            }
        }
        _dragPending = false;
        _dragNode = null;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragPending || _dragNode == null) return;

        var pos = e.GetPosition(ObjectExplorerTree);
        var delta = pos - _dragStartPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragPending = false;
        var node = _dragNode;
        _dragNode = null;

#pragma warning disable CS0618 // DataObject/DoDragDrop obsolete
        var data = new DataObject();
        data.Set("ObjectExplorerNode", node);

        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
#pragma warning restore CS0618
    }

    // ── Context Menu + Double-Click ──────────────────────────────────

    private void ShowContextMenu(ObjectExplorerNode node, Control target)
    {
        if (_viewModel == null) return;

        var explorer = _viewModel.ObjectExplorer;
        var menu = new MenuFlyout();

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Connection:
                menu.Items.Add(CreateMenuItem("New Query", () =>
                {
                    var connStr = _registry?.GetConnectionString(node.ConnectionId!);
                    var managed = _registry?.GetById(node.ConnectionId!);
                    if (connStr != null)
                        AddNewTab(connStr, managed?.Config);
                }));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Disconnect", () =>
                {
                    if (_registry == null || node.ConnectionId == null) return;
                    _registry.Disconnect(node.ConnectionId);
                    explorer.RootNodes.Remove(node);
                }));
                break;

            case ObjectExplorerNodeType.Table:
                menu.Items.Add(CreateMenuItem("SELECT TOP 100", () => explorer.SelectTop100(node)));
                menu.Items.Add(CreateMenuItem("SELECT COUNT(*)", () => explorer.SelectCount(node)));
                menu.Items.Add(CreateMenuItem("Edit Data", () => explorer.EditData(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as CREATE", () => _ = explorer.ScriptTableAsCreateAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as INSERT", () => _ = explorer.ScriptAsInsertAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as DROP", () => explorer.ScriptAsDrop(node)));
                menu.Items.Add(CreateMenuItem("Script as ALTER (add column)", () => explorer.ScriptAsAlterTable(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Properties", () => _ = ShowTablePropertiesAsync(node)));
                break;

            case ObjectExplorerNodeType.View:
                menu.Items.Add(CreateMenuItem("SELECT TOP 100", () => explorer.SelectTop100(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Show Dependencies", () => _ = explorer.ShowDependenciesAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as CREATE", () => _ = explorer.ViewDefinitionAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as ALTER", () => _ = explorer.ScriptAsAlterAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as DROP", () => explorer.ScriptAsDrop(node)));
                break;

            case ObjectExplorerNodeType.Proc:
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                menu.Items.Add(CreateMenuItem("Generate EXEC", () => _ = explorer.GenerateExecAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Show Dependencies", () => _ = explorer.ShowDependenciesAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as ALTER", () => _ = explorer.ScriptAsAlterAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as DROP", () => explorer.ScriptAsDrop(node)));
                break;

            case ObjectExplorerNodeType.Function:
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Show Dependencies", () => _ = explorer.ShowDependenciesAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as ALTER", () => _ = explorer.ScriptAsAlterAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as DROP", () => explorer.ScriptAsDrop(node)));
                break;

            case ObjectExplorerNodeType.Sequence:
                menu.Items.Add(CreateMenuItem("SELECT Current Value", () => explorer.SelectSequenceValue(node)));
                menu.Items.Add(CreateMenuItem("Script as CREATE", () => explorer.ScriptSequenceCreate(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Alter Next Value...", () => explorer.RequestAlterSequence(node)));
                menu.Items.Add(CreateMenuItem("Reset to 0", () => explorer.RequestResetSequence(node)));
                break;

            case ObjectExplorerNodeType.Trigger:
                menu.Items.Add(CreateMenuItem("View Definition", () => _ = explorer.ViewDefinitionAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Show Dependencies", () => _ = explorer.ShowDependenciesAsync(node)));
                menu.Items.Add(new Separator());
                var isDisabled = node.TypeInfo.Contains("Disabled");
                menu.Items.Add(CreateMenuItem(isDisabled ? "Enable Trigger" : "Disable Trigger",
                    () => explorer.ToggleTrigger(node, isDisabled)));
                menu.Items.Add(CreateMenuItem("Script as ALTER", () => _ = explorer.ScriptAsAlterAsync(node)));
                menu.Items.Add(CreateMenuItem("Script as DROP", () => explorer.ScriptAsDrop(node)));
                break;

            case ObjectExplorerNodeType.Job:
                menu.Items.Add(CreateMenuItem("View Job Steps", () => _ = explorer.ViewJobStepsAsync(node)));
                menu.Items.Add(CreateMenuItem("View History", () => _ = explorer.ViewJobHistoryAsync(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Start Job", () => explorer.RequestStartJob(node)));
                menu.Items.Add(CreateMenuItem("Refresh", () => RefreshParentJobsFolder(node)));
                break;

            case ObjectExplorerNodeType.Column:
                menu.Items.Add(CreateMenuItem("Copy Column Name", () => explorer.CopyColumnName(node)));
                menu.Items.Add(CreateMenuItem("Insert Column Name", () => explorer.InsertColumnName(node)));
                menu.Items.Add(CreateMenuItem("SELECT DISTINCT", () => explorer.SelectDistinct(node)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Script as SELECT", () => explorer.ScriptColumnAsSelect(node)));
                menu.Items.Add(CreateMenuItem("Script as WHERE", () => explorer.ScriptColumnAsWhere(node)));
                break;

            case ObjectExplorerNodeType.Database:
                menu.Items.Add(CreateMenuItem("New Query", () =>
                {
                    var connStr = _registry?.GetConnectionString(node.ConnectionId!);
                    var managed = _registry?.GetById(node.ConnectionId!);
                    if (connStr != null)
                    {
                        AddNewTab(connStr, managed?.Config);
                        if (ActiveTabViewModel != null)
                            ActiveTabViewModel.SelectedDatabase = node.DatabaseName;
                    }
                }));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Refresh", () => explorer.RefreshNode(node)));
                break;

            case ObjectExplorerNodeType.Folder:
                menu.Items.Add(CreateMenuItem("Refresh", () => explorer.RefreshNode(node)));
                break;

            default:
                return;
        }

        menu.ShowAt(target, true);
    }

    private async Task ShowTablePropertiesAsync(ObjectExplorerNode node)
    {
        try
        {
            if (_db == null) return;
            var connStr = _registry?.GetConnectionString(node.ConnectionId!);
            if (connStr == null) return;

            var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
            var props = await _db.GetTablePropertiesAsync(connStr, node.DatabaseName, schema, node.Name);
            if (props == null) return;

            var text = $"Table:        [{schema}].[{node.Name}]\n" +
                       $"Database:     {node.DatabaseName}\n\n" +
                       $"Rows:         {props.RowCount:N0}\n" +
                       $"Data Size:    {props.DataSizeMB:F2} MB\n" +
                       $"Index Size:   {props.IndexSizeMB:F2} MB\n" +
                       $"Columns:      {props.ColumnCount}\n" +
                       $"Indexes:      {props.IndexCount}\n" +
                       $"Created:      {props.CreateDate:yyyy-MM-dd HH:mm}\n" +
                       $"Modified:     {props.ModifyDate?.ToString("yyyy-MM-dd HH:mm") ?? "\u2014"}";

            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent == null) return;
            var dialog = new ConfirmDialog(text, "Close", "");
            dialog.Title = $"Table Properties \u2014 [{schema}].[{node.Name}]";
            dialog.Width = 400;
            dialog.Height = 300;
            // Make text monospace for alignment + hide the empty cancel button
            if (dialog.FindControl<Avalonia.Controls.TextBlock>("MessageText") is { } msgText)
                msgText.FontFamily = new Avalonia.Media.FontFamily("Consolas, Menlo, Monaco, monospace");
            if (dialog.FindControl<Button>("OkButton") is { } okBtn)
                okBtn.Classes.Remove("btn-danger");
            if (dialog.FindControl<Button>("CancelButton") is { } cancelBtn)
                cancelBtn.IsVisible = false;
            await dialog.ShowDialog(parent);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Table properties failed: {ex.Message}");
        }
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Reset drag state on any button release — prevents drag from stealing double-click
        _dragPending = false;
        _dragNode = null;

        if (e.Source is not Visual visual) return;
        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();

        // Right-click on empty space → "New Connection" menu
        if (treeViewItem == null && e.InitialPressMouseButton == MouseButton.Right)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(CreateMenuItem("New Connection...", () => NewConnectionRequested?.Invoke()));
            menu.ShowAt(ObjectExplorerTree, true);
            e.Handled = true;
            return;
        }

        if (treeViewItem?.DataContext is not ObjectExplorerNode node) return;

        // Left-click on "Back to Object Explorer" in dependency mode
        if (e.InitialPressMouseButton == MouseButton.Left && _viewModel?.ObjectExplorer.IsDependencyMode == true
            && node.Name.StartsWith("\u25c0"))
        {
            _viewModel.ObjectExplorer.BackFromDependencies();
            e.Handled = true;
            return;
        }

        // Left-click on container nodes (Connection, Database, Folder) → toggle expand
        if (e.InitialPressMouseButton == MouseButton.Left
            && node.NodeType is ObjectExplorerNodeType.Connection
                or ObjectExplorerNodeType.Database
                or ObjectExplorerNodeType.Folder
            && e.Source is Visual src
            && src.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>() == null)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right) return;

        ShowContextMenu(node, treeViewItem);
        e.Handled = true;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null || e.Source is not Visual visual)
            return;

        var treeViewItem = visual.FindAncestorOfType<TreeViewItem>();
        if (treeViewItem?.DataContext is not ObjectExplorerNode node)
            return;

        var explorer = _viewModel.ObjectExplorer;

        // In dependency mode, double-click peeks definition
        if (explorer.IsDependencyMode && node.NodeType is ObjectExplorerNodeType.Proc
            or ObjectExplorerNodeType.Function or ObjectExplorerNodeType.View
            or ObjectExplorerNodeType.Trigger)
        {
            _ = explorer.ViewDefinitionAsync(node);
            e.Handled = true;
            return;
        }

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Table:
                explorer.SelectTop100(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.View:
                explorer.SelectTop100(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Proc:
                _ = explorer.ViewDefinitionAsync(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Function:
                _ = explorer.ViewDefinitionAsync(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Column:
                explorer.InsertColumnName(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Trigger:
                _ = explorer.ViewDefinitionAsync(node);
                e.Handled = true;
                break;
            case ObjectExplorerNodeType.Sequence:
                explorer.SelectSequenceValue(node);
                e.Handled = true;
                break;
        }
    }
}
