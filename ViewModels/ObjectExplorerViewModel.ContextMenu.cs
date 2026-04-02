using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel
{
    // ── Context Menu Actions ────────────────────────────────────────

    private void FireInsertText(string sql, bool autoRun, ObjectExplorerNode node)
    {
        InsertTextRequested?.Invoke(sql, autoRun, node.DatabaseName, node.ConnectionId);
    }

    public void SelectTop100(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 100 * FROM [{schema}].[{node.Name}]";
        FireInsertText(sql, true, node);
    }

    public void SelectCount(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT COUNT(*) FROM [{schema}].[{node.Name}]";
        FireInsertText(sql, true, node);
    }

    public async Task ViewDefinitionAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var definition = connStr != null
            ? await _db.GetObjectDefinitionAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition != null)
            FireInsertText(definition, false, node);
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
        var connStr = ResolveConnectionString(node);
        var definition = connStr != null
            ? await _db.GetObjectDefinitionAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetObjectDefinitionAsync(node.DatabaseName, schema, node.Name);
        if (definition == null) return;

        // Replace CREATE with ALTER (skip if already ALTER)
        var altered = System.Text.RegularExpressions.Regex.Replace(
            definition,
            @"\bCREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b",
            "ALTER $2",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        FireInsertText(altered, false, node);
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
            ObjectExplorerNodeType.Trigger => "TRIGGER",
            _ => "OBJECT"
        };
        var sql = $"DROP {objectType} IF EXISTS [{schema}].[{node.Name}]";
        FireInsertText(sql, false, node);
    }

    public void ToggleTrigger(ObjectExplorerNode node, bool enable)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var parentTable = node.ParentTableName;
        var action = enable ? "ENABLE" : "DISABLE";
        var sql = $"{action} TRIGGER [{schema}].[{node.Name}] ON [{schema}].[{parentTable}]";
        FireInsertText(sql, false, node);
    }

    /// <summary>Generate INSERT template with all columns and placeholder values.</summary>
    public async Task ScriptAsInsertAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var colNames = string.Join(",\n    ", columns.Select(c => $"[{c.Name}]"));
        var colValues = string.Join(",\n    ", columns.Select(c => $"/* {c.TypeName} */ NULL"));
        var sql = $"INSERT INTO [{schema}].[{node.Name}]\n(\n    {colNames}\n)\nVALUES\n(\n    {colValues}\n)";
        FireInsertText(sql, false, node);
    }

    /// <summary>Generate ALTER TABLE ADD column template.</summary>
    public void ScriptAsAlterTable(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"ALTER TABLE [{schema}].[{node.Name}]\n    ADD [ColumnName] NVARCHAR(50) NULL";
        FireInsertText(sql, false, node);
    }

    /// <summary>Generate CREATE TABLE script from column metadata.</summary>
    public async Task ScriptTableAsCreateAsync(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var connStr = ResolveConnectionString(node);
        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetColumnsAsync(node.DatabaseName, schema, node.Name);

        if (columns.Count == 0) return;

        var sql = DatabaseService.GenerateCreateTableScript(schema, node.Name, columns);
        FireInsertText(sql, false, node);
    }

    /// <summary>Script column as SELECT with this column.</summary>
    public void ScriptColumnAsSelect(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT [{node.Name}] FROM [{schema}].[{table}]";
        FireInsertText(sql, false, node);
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
        var connStr = ResolveConnectionString(node);
        var parameters = connStr != null
            ? await _db.GetProcParametersAsync(connStr, node.DatabaseName, schema, node.Name)
            : await _db.GetProcParametersAsync(node.DatabaseName, schema, node.Name);

        var sql = $"EXEC [{schema}].[{node.Name}]";
        if (parameters.Count > 0)
        {
            var paramList = string.Join(",\n     ",
                parameters.Select(p => $"{p.Name} = NULL /* {p.TypeName} */"));
            sql += "\n     " + paramList;
        }

        FireInsertText(sql, false, node);
    }

    public void SelectDistinct(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var table = node.ParentTableName;
        var sql = $"SELECT DISTINCT [{node.Name}] FROM [{schema}].[{table}] ORDER BY [{node.Name}]";
        FireInsertText(sql, true, node);
    }

    public void InsertColumnName(ObjectExplorerNode node)
    {
        InsertAtCursorRequested?.Invoke($"[{node.Name}]");
    }

    public void SelectSequenceValue(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT current_value FROM [{node.DatabaseName}].sys.sequences WHERE name = '{node.Name}' AND schema_id = SCHEMA_ID('{schema}')";
        FireInsertText(sql, true, node);
    }

    public void ScriptSequenceCreate(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var dataType = node.TypeInfo.Split(',')[0].Trim();
        var sql = $"CREATE SEQUENCE [{schema}].[{node.Name}] AS {dataType}";
        FireInsertText(sql, false, node);
    }

    public async Task ViewJobStepsAsync(ObjectExplorerNode node)
    {
        var connStr = ResolveConnectionString(node);
        var steps = connStr != null
            ? await _db.GetJobStepsAsync(connStr, node.Name)
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

        FireInsertText(sb.ToString(), false, node);
    }

    public async Task ViewJobHistoryAsync(ObjectExplorerNode node)
    {
        var connStr = ResolveConnectionString(node);
        var history = connStr != null
            ? await _db.GetJobHistoryAsync(connStr, node.Name)
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

        FireInsertText(sb.ToString(), false, node);
    }

    public void EditData(ObjectExplorerNode node)
    {
        var schema = string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema;
        var sql = $"SELECT TOP 200 * FROM [{schema}].[{node.Name}]";
        EditDataRequested?.Invoke(sql, node.DatabaseName, node.ConnectionId);
    }
}
