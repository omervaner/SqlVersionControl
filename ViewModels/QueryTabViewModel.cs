using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public enum QueryStatusSeverity { None, Success, Warning, Error }

public partial class QueryTabViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly DataEditService _editService;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private ObservableCollection<string> _databases = [];
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private ObservableCollection<QueryResult> _results = [];
    [ObservableProperty] private int _selectedResultIndex;
    [ObservableProperty] private string _messages = "";
    [ObservableProperty] private bool _isRunning;

    // Trace support (Mode 1 — Quick Trace)
    [ObservableProperty] private ObservableCollection<TraceEvent> _traceEvents = [];
    [ObservableProperty] private TraceEvent? _selectedTraceEvent;
    [ObservableProperty] private bool _isTracing;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _tabTitle = "Query 1";
    [ObservableProperty] private string _queryStatusText = "";

    /// <summary>Fired with (message, severity) for transient status flash in the status bar.</summary>
    public event Action<string, QueryStatusSeverity>? QueryFlash;

    // SqlText and SelectedSqlText are set by the View (AvaloniaEdit doesn't support two-way binding)
    public string SelectedSqlText { get; set; } = "";

    private string _sqlText = "";
    private string _cleanText = ""; // Text state considered "saved" (initial or after save)
    private bool _initialized;

    public string SqlText
    {
        get => _sqlText;
        set
        {
            _sqlText = value;
            if (_initialized)
                HasUnsavedChanges = _sqlText != _cleanText;
        }
    }

    [ObservableProperty] private bool _hasUnsavedChanges;

    // ── Saved Query State ────────────────────────────────────────────
    public string? CurrentQueryPath { get; set; }
    public string? CurrentQueryName { get; set; }

    /// <summary>Mark current text as "clean" (e.g. after save).</summary>
    public void MarkClean()
    {
        _cleanText = _sqlText;
        HasUnsavedChanges = false;
    }

    /// <summary>Set initial editor text without marking as dirty.</summary>
    public void SetInitialText(string text)
    {
        _sqlText = text;
        _cleanText = text;
        _initialized = true;
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Save query to disk. Returns false if no path is set (caller should show Save As dialog).
    /// </summary>
    public bool Save(Services.QueryFileService svc, Services.SettingsService settings)
    {
        if (CurrentQueryPath == null || CurrentQueryName == null)
            return false;

        svc.SaveQuery(CurrentQueryPath, CurrentQueryName, SelectedDatabase ?? "", SqlText);
        MarkClean();
        settings.AddRecentQuery(CurrentQueryPath);
        UpdateTabTitle();
        return true;
    }

    /// <summary>
    /// Load a .sql file into this tab.
    /// </summary>
    public void LoadFromFile(string path, Services.QueryFileService svc, Services.SettingsService settings)
    {
        var (name, database, sql, _, _) = svc.LoadQuery(path);
        CurrentQueryPath = path;
        CurrentQueryName = name;
        SetInitialText(sql);

        // Try to select the saved database
        if (!string.IsNullOrEmpty(database) && Databases.Contains(database))
            SelectedDatabase = database;

        settings.AddRecentQuery(path);
        UpdateTabTitle();
    }

    private void UpdateTabTitle()
    {
        if (CurrentQueryName != null)
            TabTitle = HasUnsavedChanges ? $"{CurrentQueryName} *" : CurrentQueryName;
    }

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        UpdateTabTitle();
    }

    // ── Edit Mode ───────────────────────────────────────────────────

    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _canEditMode;
    [ObservableProperty] private int _pendingChangeCount;

    private string? _editTableSchema;
    private string? _editTableName;
    private List<string>? _editPkColumns;

    public string? EditTableSchema => _editTableSchema;
    public string? EditTableName => _editTableName;
    private string? _lastExecutedSql;
    private bool _suppressNextFlash;

    /// <summary>The editable row wrappers (set when entering edit mode).</summary>
    public ObservableCollection<EditableRow>? EditableRows { get; private set; }

    /// <summary>Column names from the current result (needed for DML generation).</summary>
    public string[]? EditColumnNames { get; private set; }

    /// <summary>When true, auto-enter edit mode after the next query finishes.</summary>
    public bool AutoEnterEditMode { get; set; }

    /// <summary>Fired when edit mode state changes (view should reconfigure the grid).</summary>
    public event Action? EditModeChanged;

    /// <summary>Fired after a successful query execution (sql, database, totalRows).</summary>
    public event Action<string, string?, int>? QueryExecuted;

    // ── Per-Tab Connection (v1.6.0) ─────────────────────────────────

    /// <summary>Full connection string for this tab's server (null = use global).</summary>
    public string? TabConnectionString { get; set; }

    /// <summary>The named profile for display (name, color, server info).</summary>
    public SavedConnection? TabConnectionProfile { get; set; }

    /// <summary>Display name for status bar.</summary>
    public string TabConnectionDisplay =>
        TabConnectionProfile?.DisplayName ?? "Disconnected";

    /// <summary>Color hex for status bar/stripe.</summary>
    public string TabConnectionColor =>
        TabConnectionProfile?.Color ?? "#88a1bb";

    public string GetEffectiveConnectionString(string database)
    {
        if (TabConnectionString != null)
            return DatabaseService.BuildConnectionString(TabConnectionString, database);
        return _db.GetConnectionStringForDatabase(database);
    }

    public QueryTabViewModel(DatabaseService db)
    {
        _db = db;
        _editService = new DataEditService(db);
    }

    /// <summary>
    /// Set the database list (called by host when databases load or when creating a new tab).
    /// </summary>
    public void SetDatabases(IEnumerable<string> databases, string? selectedDb = null)
    {
        Databases = new ObservableCollection<string>(databases);
        if (selectedDb != null && Databases.Contains(selectedDb))
            SelectedDatabase = selectedDb;
        else if (SelectedDatabase != null && Databases.Contains(SelectedDatabase))
            { } // keep current selection
        else if (Databases.Count > 0)
            SelectedDatabase = Databases[0];
    }

    [RelayCommand]
    private async Task RunQueryAsync()
    {
        if (IsRunning || string.IsNullOrEmpty(SelectedDatabase)) return;

        var sql = !string.IsNullOrWhiteSpace(SelectedSqlText)
            ? SelectedSqlText
            : SqlText;

        if (string.IsNullOrWhiteSpace(sql)) return;

        // Exit edit mode before running a new query
        if (IsEditMode)
            ExitEditMode();

        IsRunning = true;
        StatusText = "Executing...";
        QueryStatusText = "Running...";
        Results.Clear();
        Messages = "";
        _cts = new CancellationTokenSource();
        _lastExecutedSql = sql;
        var sw = Stopwatch.StartNew();

        try
        {
            var (results, messages) = TabConnectionString != null
                ? await _db.ExecuteQueryAsync(TabConnectionString, SelectedDatabase!, sql, _cts.Token)
                : await _db.ExecuteQueryAsync(SelectedDatabase!, sql, _cts.Token);
            sw.Stop();

            foreach (var r in results)
            {
                r.SourceSql = sql;
                Results.Add(r);
            }

            Messages = messages;

            if (Results.Count > 0)
                SelectedResultIndex = 0;

            var totalRows = results.Where(r => r.Error == null).Sum(r => r.RowCount);
            var elapsed = sw.ElapsedMilliseconds < 1000
                ? $"{sw.ElapsedMilliseconds}ms"
                : $"{sw.Elapsed.TotalSeconds:F1}s";
            StatusText = totalRows > 10_000
                ? $"{Results.Count} result set(s), {totalRows:N0} total rows (large)"
                : $"{Results.Count} result set(s), {totalRows:N0} total rows";
            QueryStatusText = $"{totalRows:N0} rows, {elapsed}";

            if (!_suppressNextFlash)
            {
                var hasColumns = results.Any(r => r.Error == null && r.ColumnNames.Length > 0);
                QueryFlash?.Invoke(
                    hasColumns ? $"\u2713 {totalRows:N0} rows" : $"\u2713 {totalRows:N0} rows affected",
                    QueryStatusSeverity.Success);
            }
            _suppressNextFlash = false;

            // Record in query history
            QueryExecuted?.Invoke(sql, SelectedDatabase, totalRows);

            // Check if result is eligible for edit mode
            CheckEditEligibility(sql);

            // Auto-enter edit mode if requested (e.g. from "Edit Data" context menu)
            if (AutoEnterEditMode && CanEditMode)
            {
                AutoEnterEditMode = false;
                await EnterEditModeAsync();
            }
            else
            {
                AutoEnterEditMode = false;
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            StatusText = "Query cancelled";
            QueryStatusText = "";
            QueryFlash?.Invoke("\u2298 Cancelled", QueryStatusSeverity.Warning);
            AutoEnterEditMode = false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Messages = $"Error: {ex.Message}";
            StatusText = "Error";
            QueryStatusText = "";
            // Try to extract line number from SQL error (e.g. "Line 12")
            var lineMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Line (\d+)");
            QueryFlash?.Invoke(
                lineMatch.Success ? $"\u2717 Error (Line {lineMatch.Groups[1].Value})" : "\u2717 Error",
                QueryStatusSeverity.Error);
            AutoEnterEditMode = false;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void StopQuery()
    {
        _cts?.Cancel();
    }

    // ── Trace Mode (Mode 1 — Quick Trace) ────────────────────────────

    [RelayCommand]
    private async Task RunWithTraceAsync()
    {
        if (IsRunning || string.IsNullOrEmpty(SelectedDatabase)) return;

        var sql = !string.IsNullOrWhiteSpace(SelectedSqlText)
            ? SelectedSqlText
            : SqlText;

        if (string.IsNullOrWhiteSpace(sql)) return;

        if (IsEditMode)
            ExitEditMode();

        // Check permission first
        var traceService = new TraceService();
        var serverConnStr = TabConnectionString ?? _db.GetConnectionStringForDatabase("master");
        if (!await traceService.HasTracePermissionAsync(serverConnStr))
        {
            Messages = "Trace requires ALTER ANY EVENT SESSION permission on the server.";
            QueryFlash?.Invoke("\u2717 No trace permission", QueryStatusSeverity.Error);
            return;
        }

        IsRunning = true;
        IsTracing = true;
        StatusText = "Tracing...";
        QueryStatusText = "Tracing...";
        Results.Clear();
        TraceEvents.Clear();
        Messages = "";
        _cts = new CancellationTokenSource();
        _lastExecutedSql = sql;
        var sw = Stopwatch.StartNew();

        try
        {
            var connStr = TabConnectionString ?? _db.GetConnectionStringForDatabase(SelectedDatabase!);
            var (results, messages, traceEvents) = await _db.ExecuteWithTraceAsync(
                TabConnectionString ?? connStr, SelectedDatabase!, sql, _cts.Token, traceService);
            sw.Stop();

            foreach (var r in results)
            {
                r.SourceSql = sql;
                Results.Add(r);
            }

            foreach (var te in traceEvents)
                TraceEvents.Add(te);

            Messages = messages;

            if (Results.Count > 0)
                SelectedResultIndex = 0;

            var totalRows = results.Where(r => r.Error == null).Sum(r => r.RowCount);
            var elapsed = sw.ElapsedMilliseconds < 1000
                ? $"{sw.ElapsedMilliseconds}ms"
                : $"{sw.Elapsed.TotalSeconds:F1}s";
            StatusText = $"{Results.Count} result(s), {totalRows:N0} rows, {TraceEvents.Count} traced";
            QueryStatusText = $"{totalRows:N0} rows, {elapsed}, {TraceEvents.Count} traced";

            QueryFlash?.Invoke(
                $"\u2713 {totalRows:N0} rows, {TraceEvents.Count} traced",
                QueryStatusSeverity.Success);

            QueryExecuted?.Invoke(sql, SelectedDatabase, totalRows);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            StatusText = "Trace cancelled";
            QueryStatusText = "";
            QueryFlash?.Invoke("\u2298 Cancelled", QueryStatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Messages = $"Error: {ex.Message}";
            StatusText = "Error";
            QueryStatusText = "";
            QueryFlash?.Invoke("\u2717 Error", QueryStatusSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            IsTracing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ── Edit Mode Logic ─────────────────────────────────────────────

    private void CheckEditEligibility(string sql)
    {
        CanEditMode = false;
        _editTableSchema = null;
        _editTableName = null;

        if (Results.Count == 0 || Results[0].Error != null) return;

        var (schema, table) = DataEditService.ParseSimpleSelect(sql);
        if (schema == null || table == null) return;

        _editTableSchema = schema;
        _editTableName = table;
        CanEditMode = true;
    }

    [RelayCommand]
    private async Task ToggleEditModeAsync()
    {
        if (IsEditMode)
            ExitEditMode();
        else
            await EnterEditModeAsync();
    }

    private async Task EnterEditModeAsync()
    {
        if (!CanEditMode || SelectedDatabase == null ||
            _editTableSchema == null || _editTableName == null) return;

        if (Results.Count == 0 || Results[0].Error != null) return;

        try
        {
            // Fetch PK columns
            _editPkColumns = TabConnectionString != null
                ? await _editService.GetPrimaryKeyColumnsFromConnAsync(
                      GetEffectiveConnectionString(SelectedDatabase!), _editTableSchema, _editTableName)
                : await _editService.GetPrimaryKeyColumnsAsync(
                      SelectedDatabase!, _editTableSchema, _editTableName);

            if (_editPkColumns.Count == 0)
            {
                StatusText = "Table has no primary key — edit mode unavailable";
                return;
            }

            // Verify PK columns exist in the result set
            var result = Results[SelectedResultIndex >= 0 && SelectedResultIndex < Results.Count
                ? SelectedResultIndex : 0];
            EditColumnNames = result.ColumnNames;

            var missingPks = _editPkColumns
                .Where(pk => !EditColumnNames.Any(c => c.Equals(pk, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (missingPks.Count > 0)
            {
                StatusText = $"PK column(s) not in result: {string.Join(", ", missingPks)}. Include them in your SELECT.";
                return;
            }

            // Wrap rows in EditableRow
            EditableRows = new ObservableCollection<EditableRow>(
                result.Rows.Select(r => new EditableRow(r, result.ColumnTypes))
            );

            IsEditMode = true;
            PendingChangeCount = 0;
            StatusText = $"Edit mode — {_editTableSchema}.{_editTableName}";
            EditModeChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to enter edit mode: {ex.Message}";
        }
    }

    private void ExitEditMode()
    {
        IsEditMode = false;
        EditableRows = null;
        EditColumnNames = null;
        _editPkColumns = null;
        PendingChangeCount = 0;
        StatusText = "";
        EditModeChanged?.Invoke();
    }

    /// <summary>
    /// Recalculate pending change count from editable rows.
    /// Called by the view after row edit events and after marking rows for delete.
    /// </summary>
    public void UpdatePendingChangeCount()
    {
        if (EditableRows == null) { PendingChangeCount = 0; return; }
        PendingChangeCount = EditableRows.Count(r => r.State != RowEditState.None);
    }

    [RelayCommand]
    private void AddNewRow()
    {
        if (!IsEditMode || EditableRows == null || EditColumnNames == null) return;

        var result = Results.Count > 0 ? Results[0] : null;
        if (result == null) return;

        var emptyValues = new object?[EditColumnNames.Length];
        var newRow = new EditableRow(emptyValues, result.ColumnTypes, RowEditState.New);
        EditableRows.Add(newRow);
        UpdatePendingChangeCount();
    }

    /// <summary>
    /// Paste parsed TSV rows into the editable grid, starting at the given row index.
    /// Overwrites existing rows (marking Modified) and appends new rows beyond the end.
    /// </summary>
    public void PasteRows(List<string[]> parsedRows, int startIndex)
    {
        if (!IsEditMode || EditableRows == null || EditColumnNames == null) return;

        var result = Results.Count > 0 ? Results[0] : null;
        if (result == null) return;

        var colCount = EditColumnNames.Length;

        for (int r = 0; r < parsedRows.Count; r++)
        {
            var cells = parsedRows[r];
            var targetIndex = startIndex + r;

            if (targetIndex < EditableRows.Count)
            {
                // Overwrite existing row (skip deleted rows)
                var existing = EditableRows[targetIndex];
                if (existing.State == RowEditState.Deleted) continue;

                existing.TakeSnapshot();
                for (int c = 0; c < cells.Length && c < colCount; c++)
                    existing[c] = cells[c];

                if (existing.State != RowEditState.New)
                    existing.State = existing.HasChanges() ? RowEditState.Modified : RowEditState.None;
            }
            else
            {
                // Append new row
                var values = new object?[colCount];
                var newRow = new EditableRow(values, result.ColumnTypes, RowEditState.New);
                for (int c = 0; c < cells.Length && c < colCount; c++)
                    newRow[c] = cells[c];
                EditableRows.Add(newRow);
            }
        }

        UpdatePendingChangeCount();
    }

    /// <summary>
    /// Undo the edit state of a single row: revert Modified, remove New, un-delete Deleted.
    /// </summary>
    public void UndoRow(EditableRow row)
    {
        if (!IsEditMode || EditableRows == null) return;

        switch (row.State)
        {
            case RowEditState.New:
                EditableRows.Remove(row);
                break;
            case RowEditState.Modified:
                row.Revert();
                break;
            case RowEditState.Deleted:
                row.State = row.HasChanges() ? RowEditState.Modified : RowEditState.None;
                break;
        }

        UpdatePendingChangeCount();
    }

    [RelayCommand]
    private void MarkRowForDelete(EditableRow? row)
    {
        if (row == null || !IsEditMode) return;

        if (row.State == RowEditState.New)
        {
            // New row that hasn't been saved — just remove it
            EditableRows?.Remove(row);
        }
        else if (row.State == RowEditState.Deleted)
        {
            // Undelete — restore to previous state
            row.State = row.HasChanges() ? RowEditState.Modified : RowEditState.None;
        }
        else
        {
            row.State = RowEditState.Deleted;
        }

        UpdatePendingChangeCount();
    }

    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        if (!IsEditMode || EditableRows == null || EditColumnNames == null ||
            _editPkColumns == null || SelectedDatabase == null ||
            _editTableSchema == null || _editTableName == null) return;

        var pendingRows = EditableRows.Where(r => r.State != RowEditState.None).ToList();
        if (pendingRows.Count == 0)
        {
            ExitEditMode();
            return;
        }

        StatusText = "Applying changes...";

        var (success, message) = TabConnectionString != null
            ? await _editService.ApplyChangesFromConnAsync(
                  GetEffectiveConnectionString(SelectedDatabase!), _editTableSchema, _editTableName,
                  EditColumnNames, _editPkColumns, pendingRows)
            : await _editService.ApplyChangesAsync(
                  SelectedDatabase!, _editTableSchema, _editTableName,
                  EditColumnNames, _editPkColumns, pendingRows);

        if (success)
        {
            StatusText = message;
            QueryFlash?.Invoke($"\u2713 {pendingRows.Count} row{(pendingRows.Count == 1 ? "" : "s")} affected", QueryStatusSeverity.Success);
            // Re-run the query to get fresh data (suppress its flash — DML flash is already showing)
            ExitEditMode();
            if (!string.IsNullOrEmpty(_lastExecutedSql))
            {
                _suppressNextFlash = true;
                await RunQueryAsync();
            }
        }
        else
        {
            StatusText = message;
        }
    }

    [RelayCommand]
    private void CancelChanges()
    {
        if (!IsEditMode) return;
        ExitEditMode();
        // Re-display the original result
        if (Results.Count > 0)
            EditModeChanged?.Invoke(); // Signal view to rebind to original data
    }

    /// <summary>
    /// Generate SQL preview for pending changes.
    /// </summary>
    public string? GeneratePreviewSql()
    {
        if (EditableRows == null || EditColumnNames == null ||
            _editPkColumns == null || _editTableSchema == null || _editTableName == null)
            return null;

        var pendingRows = EditableRows.Where(r => r.State != RowEditState.None).ToList();
        if (pendingRows.Count == 0) return null;

        return DataEditService.GeneratePreviewSql(
            _editTableSchema, _editTableName,
            EditColumnNames, _editPkColumns, pendingRows);
    }
}
