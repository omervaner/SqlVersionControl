using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

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
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _tabTitle = "Query 1";

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
    private string? _lastExecutedSql;

    /// <summary>The editable row wrappers (set when entering edit mode).</summary>
    public ObservableCollection<EditableRow>? EditableRows { get; private set; }

    /// <summary>Column names from the current result (needed for DML generation).</summary>
    public string[]? EditColumnNames { get; private set; }

    /// <summary>When true, auto-enter edit mode after the next query finishes.</summary>
    public bool AutoEnterEditMode { get; set; }

    /// <summary>Fired when edit mode state changes (view should reconfigure the grid).</summary>
    public event Action? EditModeChanged;

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
        else if (SelectedDatabase == null && Databases.Contains("AAD"))
            SelectedDatabase = "AAD";
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
        Results.Clear();
        Messages = "";
        _cts = new CancellationTokenSource();
        _lastExecutedSql = sql;

        try
        {
            var (results, messages) = await _db.ExecuteQueryAsync(
                SelectedDatabase, sql, _cts.Token);

            foreach (var r in results)
                Results.Add(r);

            Messages = messages;

            if (Results.Count > 0)
                SelectedResultIndex = 0;

            var totalRows = results.Where(r => r.Error == null).Sum(r => r.RowCount);
            StatusText = $"{Results.Count} result set(s), {totalRows} total rows";

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
            StatusText = "Query cancelled";
            AutoEnterEditMode = false;
        }
        catch (Exception ex)
        {
            Messages = $"Error: {ex.Message}";
            StatusText = "Error";
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
            _editPkColumns = await _editService.GetPrimaryKeyColumnsAsync(
                SelectedDatabase, _editTableSchema, _editTableName);

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
        StatusText = "Ready";
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
        if (pendingRows.Count == 0) return;

        StatusText = "Applying changes...";

        var (success, message) = await _editService.ApplyChangesAsync(
            SelectedDatabase, _editTableSchema, _editTableName,
            EditColumnNames, _editPkColumns, pendingRows);

        if (success)
        {
            StatusText = message;
            // Re-run the query to get fresh data
            ExitEditMode();
            if (!string.IsNullOrEmpty(_lastExecutedSql))
                await RunQueryAsync();
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
