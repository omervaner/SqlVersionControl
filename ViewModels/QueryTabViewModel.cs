using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class QueryTabViewModel : ObservableObject
{
    private readonly DatabaseService _db;
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

    public QueryTabViewModel(DatabaseService db)
    {
        _db = db;
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

        IsRunning = true;
        StatusText = "Executing...";
        Results.Clear();
        Messages = "";
        _cts = new CancellationTokenSource();

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
        }
        catch (OperationCanceledException)
        {
            StatusText = "Query cancelled";
        }
        catch (Exception ex)
        {
            Messages = $"Error: {ex.Message}";
            StatusText = "Error";
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
}
