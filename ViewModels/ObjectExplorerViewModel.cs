using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private Timer? _filterDebounce;

    [ObservableProperty] private ObservableCollection<ObjectExplorerNode> _rootNodes = [];
    [ObservableProperty] private string _filterText = "";

    /// <summary>Fired when a context menu action wants to set editor text. Bool = auto-run.</summary>
    public event Action<string, bool>? InsertTextRequested;

    /// <summary>Fired when column double-click wants to insert text at cursor.</summary>
    public event Action<string>? InsertAtCursorRequested;

    /// <summary>Fired when "Edit Data" wants to run a SELECT and auto-enter edit mode.</summary>
    public event Action<string>? EditDataRequested;

    /// <summary>Fired when a sequence context menu wants to show the Alter dialog.</summary>
    public event Action<ObjectExplorerNode>? AlterSequenceRequested;

    /// <summary>Fired when "Reset to 0" wants confirmation and execution.</summary>
    public event Action<ObjectExplorerNode>? ResetSequenceRequested;

    /// <summary>Fired when "Start Job" wants confirmation and execution.</summary>
    public event Action<ObjectExplorerNode>? StartJobRequested;

    public void RequestAlterSequence(ObjectExplorerNode node) => AlterSequenceRequested?.Invoke(node);
    public void RequestResetSequence(ObjectExplorerNode node) => ResetSequenceRequested?.Invoke(node);
    public void RequestStartJob(ObjectExplorerNode node) => StartJobRequested?.Invoke(node);

    private string? _activeConnectionString;

    public void SetActiveConnection(string? connectionString)
    {
        _activeConnectionString = connectionString;
    }

    public ObjectExplorerViewModel(DatabaseService db)
    {
        _db = db;
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Dispose();
        _filterDebounce = new Timer(_ =>
            Dispatcher.UIThread.Post(ApplyFilter), null, 200, Timeout.Infinite);
    }

    public void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? "";
        foreach (var db in RootNodes)
            ApplyFilterToNode(db, filter);
    }

    private bool ApplyFilterToNode(ObjectExplorerNode node, string filter)
    {
        // No filter → everything visible
        if (string.IsNullOrEmpty(filter))
        {
            node.IsVisibleInFilter = true;
            foreach (var child in node.Children)
                ApplyFilterToNode(child, filter);
            return true;
        }

        switch (node.NodeType)
        {
            case ObjectExplorerNodeType.Column:
                // Columns follow parent visibility — always visible
                node.IsVisibleInFilter = true;
                return true;

            case ObjectExplorerNodeType.Table:
            case ObjectExplorerNodeType.View:
            case ObjectExplorerNodeType.Proc:
            case ObjectExplorerNodeType.Function:
            case ObjectExplorerNodeType.Sequence:
            case ObjectExplorerNodeType.Job:
                // Leaf objects: match against name
                var matches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                node.IsVisibleInFilter = matches;
                // Still recurse children (columns) so they stay visible
                foreach (var child in node.Children)
                    child.IsVisibleInFilter = true;
                return matches;

            case ObjectExplorerNodeType.Database:
            case ObjectExplorerNodeType.Folder:
                // Containers: visible if any child matches
                var anyChildVisible = false;
                foreach (var child in node.Children)
                {
                    if (ApplyFilterToNode(child, filter))
                        anyChildVisible = true;
                }
                node.IsVisibleInFilter = anyChildVisible;
                return anyChildVisible;

            default:
                node.IsVisibleInFilter = true;
                return true;
        }
    }

    [RelayCommand]
    public void ClearFilter()
    {
        FilterText = "";
    }

    public async Task LoadDatabasesAsync(IEnumerable<string> databases)
    {
        RootNodes.Clear();

        foreach (var dbName in databases)
        {
            var node = WireNode(new ObjectExplorerNode
            {
                Name = dbName,
                DatabaseName = dbName,
                NodeType = ObjectExplorerNodeType.Database,
                Children = [ObjectExplorerNode.CreateDummy()]
            });
            RootNodes.Add(node);
        }
    }

    private ObjectExplorerNode WireNode(ObjectExplorerNode node)
    {
        node.ExpandRequested += n => _ = OnNodeExpandedAsync(n);
        return node;
    }

    public async Task OnNodeExpandedAsync(ObjectExplorerNode node)
    {
        if (!node.HasDummyChild) return;

        node.Children.Clear();
        node.IsLoading = true;

        try
        {
            switch (node.NodeType)
            {
                case ObjectExplorerNodeType.Database:
                    await LoadDatabaseChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Folder:
                    await LoadFolderChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Table:
                    await LoadColumnsAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            node.Children.Add(new ObjectExplorerNode
            {
                Name = $"Error: {ex.Message}",
                NodeType = ObjectExplorerNodeType.Folder
            });
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    /// <summary>
    /// Invalidates cached children for the node and re-triggers lazy loading from the server.
    /// </summary>
    public void RefreshNode(ObjectExplorerNode node)
    {
        // Clear all loaded children and reset to dummy — this invalidates the cache
        // so HasDummyChild returns true and the next expand fetches fresh data from DB.
        node.Children.Clear();
        node.ChildCount = 0;
        node.Children.Add(ObjectExplorerNode.CreateDummy());

        // Collapse then re-expand to trigger the lazy load
        node.IsExpanded = false;
        node.IsExpanded = true;
    }

    private Task LoadDatabaseChildrenAsync(ObjectExplorerNode dbNode)
    {
        var folders = new[]
        {
            ("Tables", ObjectExplorerNodeType.Folder),
            ("Views", ObjectExplorerNodeType.Folder),
            ("Stored Procedures", ObjectExplorerNodeType.Folder),
            ("Functions", ObjectExplorerNodeType.Folder),
            ("Sequences", ObjectExplorerNodeType.Folder),
            ("Jobs", ObjectExplorerNodeType.Folder),
        };

        foreach (var (name, type) in folders)
        {
            dbNode.Children.Add(WireNode(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = dbNode.DatabaseName,
                NodeType = type,
                IsCategoryFolder = true,
                Children = [ObjectExplorerNode.CreateDummy()]
            }));
        }

        return Task.CompletedTask;
    }

    private async Task LoadFolderChildrenAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;

        switch (folderNode.Name)
        {
            case "Tables":
                var tables = _activeConnectionString != null
                    ? await _db.GetTablesAsync(_activeConnectionString, db)
                    : await _db.GetTablesAsync(db);
                var tableNodes = tables.Select(t => WireNode(new ObjectExplorerNode
                {
                    Name = t.Name, Schema = t.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Table,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }));
                await AddChildrenInBatchesAsync(folderNode, tableNodes);
                break;

            case "Views":
                var views = _activeConnectionString != null
                    ? await _db.GetViewsAsync(_activeConnectionString, db)
                    : await _db.GetViewsAsync(db);
                var viewNodes = views.Select(v => new ObjectExplorerNode
                {
                    Name = v.Name, Schema = v.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.View
                });
                await AddChildrenInBatchesAsync(folderNode, viewNodes);
                break;

            case "Stored Procedures":
                var procsAndFuncs = _activeConnectionString != null
                    ? await _db.GetProcsAndFunctionsAsync(_activeConnectionString, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var procNodes = procsAndFuncs
                    .Where(x => x.Type == "SQL_STORED_PROCEDURE")
                    .Select(p => new ObjectExplorerNode
                    {
                        Name = p.Name, Schema = p.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Proc
                    });
                await AddChildrenInBatchesAsync(folderNode, procNodes);
                break;

            case "Functions":
                var funcs = _activeConnectionString != null
                    ? await _db.GetProcsAndFunctionsAsync(_activeConnectionString, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var funcNodes = funcs
                    .Where(x => x.Type != "SQL_STORED_PROCEDURE")
                    .Select(f => new ObjectExplorerNode
                    {
                        Name = f.Name, Schema = f.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Function
                    });
                await AddChildrenInBatchesAsync(folderNode, funcNodes);
                break;

            case "Sequences":
                var sequences = _activeConnectionString != null
                    ? await _db.GetSequencesAsync(_activeConnectionString, db)
                    : await _db.GetSequencesAsync(db);
                var seqNodes = sequences.Select(seq => new ObjectExplorerNode
                {
                    Name = seq.Name, Schema = seq.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Sequence,
                    TypeInfo = $"{seq.DataType}, Current: {seq.CurrentValue}"
                });
                await AddChildrenInBatchesAsync(folderNode, seqNodes);
                break;

            case "Jobs":
                try
                {
                    var jobs = _activeConnectionString != null
                        ? await _db.GetJobsAsync(_activeConnectionString)
                        : await _db.GetJobsAsync();
                    var jobNodes = jobs.Select(j => new ObjectExplorerNode
                    {
                        Name = j.Name, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Job,
                        TypeInfo = j.Enabled
                            ? $"Enabled, Last: {j.LastOutcome}"
                            : "Disabled"
                    });
                    await AddChildrenInBatchesAsync(folderNode, jobNodes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Jobs load error: {ex}");
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = $"(Error: {ex.Message})",
                        NodeType = ObjectExplorerNodeType.Folder
                    });
                }
                break;
        }

        if (folderNode.Children.Count == 0)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(empty)",
                NodeType = ObjectExplorerNodeType.Folder
            });
        }
        else
        {
            folderNode.ChildCount = folderNode.Children.Count;
        }

        // Re-apply filter if active so new children get filtered
        if (!string.IsNullOrEmpty(FilterText))
            ApplyFilter();
    }

    private async Task AddChildrenInBatchesAsync(
        ObjectExplorerNode parent,
        IEnumerable<ObjectExplorerNode> children,
        int batchSize = 50)
    {
        var batch = new List<ObjectExplorerNode>();
        foreach (var child in children)
        {
            batch.Add(child);
            if (batch.Count >= batchSize)
            {
                var toAdd = batch.ToList();
                batch.Clear();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var node in toAdd)
                        parent.Children.Add(node);
                });
            }
        }
        if (batch.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var node in batch)
                    parent.Children.Add(node);
            });
        }
    }

    private async Task LoadColumnsAsync(ObjectExplorerNode tableNode)
    {
        var columns = _activeConnectionString != null
            ? await _db.GetColumnsAsync(_activeConnectionString, tableNode.DatabaseName, tableNode.Schema, tableNode.Name)
            : await _db.GetColumnsAsync(tableNode.DatabaseName, tableNode.Schema, tableNode.Name);

        foreach (var (name, typeName, maxLength, isNullable, isPk) in columns)
        {
            var typeInfo = FormatColumnType(typeName, maxLength);

            tableNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = tableNode.DatabaseName,
                Schema = tableNode.Schema,
                NodeType = ObjectExplorerNodeType.Column,
                TypeInfo = typeInfo,
                IsPrimaryKey = isPk,
                IsNullable = isNullable,
                ParentTableName = tableNode.Name
            });
        }
    }

    private static string FormatColumnType(string typeName, int maxLength)
        => SqlTypeFormatter.Format(typeName, maxLength);

    // ── Context Menu Actions ────────────────────────────────────────

    public void SelectTop100(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 100 * FROM [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void SelectCount(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT COUNT(*) FROM [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public async Task ViewDefinitionAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var definition = _activeConnectionString != null
            ? await _db.GetObjectDefinitionAsync(_activeConnectionString, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition != null)
            InsertTextRequested?.Invoke(definition, false);
    }

    public void ScriptAsCreate(ObjectExplorerNode node)
    {
        // Same as ViewDefinition — the definition IS the CREATE script
        _ = ViewDefinitionAsync(node);
    }

    /// <summary>Script as ALTER — fetch definition and replace CREATE with ALTER.</summary>
    public async Task ScriptAsAlterAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var definition = _activeConnectionString != null
            ? await _db.GetObjectDefinitionAsync(_activeConnectionString, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition == null) return;

        // Replace CREATE with ALTER (skip if already ALTER)
        var altered = System.Text.RegularExpressions.Regex.Replace(
            definition,
            @"\bCREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b",
            "ALTER $2",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        InsertTextRequested?.Invoke(altered, false);
    }

    /// <summary>Script as DROP with IF EXISTS safety.</summary>
    public void ScriptAsDrop(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var objectType = node.NodeType switch
        {
            ObjectExplorerNodeType.Table => "TABLE",
            ObjectExplorerNodeType.View => "VIEW",
            ObjectExplorerNodeType.Proc => "PROCEDURE",
            ObjectExplorerNodeType.Function => "FUNCTION",
            _ => "OBJECT"
        };
        var sql = $"DROP {objectType} IF EXISTS [{schema}].[{node.Name}]";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate INSERT template with all columns and placeholder values.</summary>
    public async Task ScriptAsInsertAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var columns = _activeConnectionString != null
            ? await _db.GetColumnsAsync(_activeConnectionString, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var colNames = string.Join(",\n    ", columns.Select(c => $"[{c.Name}]"));
        var colValues = string.Join(",\n    ", columns.Select(c => $"/* {c.TypeName} */ NULL"));
        var sql = $"INSERT INTO [{schema}].[{node.Name}]\n(\n    {colNames}\n)\nVALUES\n(\n    {colValues}\n)";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate ALTER TABLE ADD column template.</summary>
    public void ScriptAsAlterTable(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"ALTER TABLE [{schema}].[{node.Name}]\n    ADD [ColumnName] NVARCHAR(50) NULL";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Generate CREATE TABLE script from column metadata.</summary>
    public async Task ScriptTableAsCreateAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var columns = _activeConnectionString != null
            ? await _db.GetColumnsAsync(_activeConnectionString, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var sql = DatabaseService.GenerateCreateTableScript(schema, node.Name, columns);
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Script column as SELECT with this column.</summary>
    public void ScriptColumnAsSelect(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT [{node.Name}] FROM [{schema}].[{table}]";
        InsertTextRequested?.Invoke(sql, false);
    }

    /// <summary>Script column as WHERE clause.</summary>
    public void ScriptColumnAsWhere(ObjectExplorerNode node)
    {
        var sql = $"WHERE [{node.Name}] = ''";
        InsertAtCursorRequested?.Invoke(sql);
    }

    /// <summary>Copy column name to clipboard (handled via event).</summary>
    public event Action<string>? CopyToClipboardRequested;

    public void CopyColumnName(ObjectExplorerNode node)
    {
        CopyToClipboardRequested?.Invoke(node.Name);
    }

    public async Task GenerateExecAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var parameters = _activeConnectionString != null
            ? await _db.GetProcParametersAsync(_activeConnectionString, node.DatabaseName, schema, node.Name)
            : await _db.GetProcParametersAsync(node.DatabaseName, schema, node.Name);

        var sql = $"EXEC [{schema}].[{node.Name}]";
        if (parameters.Count > 0)
        {
            var paramList = string.Join(",\n     ",
                parameters.Select(p => $"{p.Name} = NULL /* {p.TypeName} */"));
            sql += "\n     " + paramList;
        }

        InsertTextRequested?.Invoke(sql, false);
    }

    public void SelectDistinct(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT DISTINCT [{node.Name}] FROM [{schema}].[{table}] ORDER BY [{node.Name}]";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void InsertColumnName(ObjectExplorerNode node)
    {
        InsertAtCursorRequested?.Invoke($"[{node.Name}]");
    }

    public void SelectSequenceValue(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT current_value FROM [{node.DatabaseName}].sys.sequences WHERE name = '{node.Name}' AND schema_id = SCHEMA_ID('{schema}')";
        InsertTextRequested?.Invoke(sql, true);
    }

    public void ScriptSequenceCreate(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var dataType = node.TypeInfo.Split(',')[0].Trim();
        var sql = $"CREATE SEQUENCE [{schema}].[{node.Name}] AS {dataType}";
        InsertTextRequested?.Invoke(sql, false);
    }

    public async Task ViewJobStepsAsync(ObjectExplorerNode node)
    {
        var steps = _activeConnectionString != null
            ? await _db.GetJobStepsAsync(_activeConnectionString, node.Name)
            : await _db.GetJobStepsAsync(node.Name);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Job Steps: {node.Name}");
        sb.AppendLine($"-- {steps.Count} step(s)");
        sb.AppendLine();

        foreach (var (stepId, stepName, subsystem, command) in steps)
        {
            sb.AppendLine($"-- Step {stepId}: {stepName} [{subsystem}]");
            sb.AppendLine(command);
            sb.AppendLine();
        }

        InsertTextRequested?.Invoke(sb.ToString(), false);
    }

    public async Task ViewJobHistoryAsync(ObjectExplorerNode node)
    {
        var history = _activeConnectionString != null
            ? await _db.GetJobHistoryAsync(_activeConnectionString, node.Name)
            : await _db.GetJobHistoryAsync(node.Name);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Job History: {node.Name} (last {history.Count} runs)");
        sb.AppendLine();

        foreach (var (runStatus, runDate, durationSeconds, message) in history)
        {
            var status = runStatus switch { 0 => "Failed", 1 => "Success", 2 => "Retry", 3 => "Cancelled", _ => "Unknown" };
            var duration = TimeSpan.FromSeconds(durationSeconds);
            sb.AppendLine($"-- {runDate:yyyy-MM-dd HH:mm:ss}  {status}  Duration: {duration:hh\\:mm\\:ss}");
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.AppendLine($"--   {message}");
            }
            sb.AppendLine();
        }

        InsertTextRequested?.Invoke(sb.ToString(), false);
    }

    public void EditData(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 200 * FROM [{schema}].[{node.Name}]";
        EditDataRequested?.Invoke(sql);
    }

    /// <summary>
    /// Restore OE tree from cached nodes (for fast tab-switch).
    /// </summary>
    public void RestoreNodes(List<ObjectExplorerNode> nodes)
    {
        RootNodes.Clear();
        foreach (var node in nodes)
            RootNodes.Add(node);
    }
}
