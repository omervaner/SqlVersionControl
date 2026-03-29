using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SqlVersionControl.Services;

namespace SqlVersionControl.Views;

public partial class IndexAnalysisDialog : Window
{
    private readonly DatabaseService _db = null!;
    private readonly string _connectionString = "";
    private string? _selectedDatabase;

    // Raw data (unfiltered)
    private List<Dictionary<string, object?>> _unusedIndexes = [];
    private List<Dictionary<string, object?>> _missingIndexes = [];
    private List<Dictionary<string, object?>> _duplicateIndexes = [];

    public IndexAnalysisDialog() { InitializeComponent(); }

    public IndexAnalysisDialog(DatabaseService db, string connectionString, List<string> databases, string? currentDatabase)
    {
        _db = db;
        _connectionString = connectionString;

        InitializeComponent();

        // Populate database dropdown
        foreach (var dbName in databases)
            DatabaseDropdown.Items.Add(dbName);

        if (currentDatabase != null && databases.Contains(currentDatabase))
            DatabaseDropdown.SelectedItem = currentDatabase;
        else if (databases.Count > 0)
            DatabaseDropdown.SelectedIndex = 0;

        _selectedDatabase = DatabaseDropdown.SelectedItem as string;

        // Events
        DatabaseDropdown.SelectionChanged += (_, _) =>
        {
            _selectedDatabase = DatabaseDropdown.SelectedItem as string;
            _ = LoadDataAsync();
        };

        RefreshButton.Click += (_, _) => _ = LoadDataAsync();

        // Filters for Unused tab
        HideClustered.IsCheckedChanged += (_, _) => ApplyUnusedFilters();
        HidePrimaryKeys.IsCheckedChanged += (_, _) => ApplyUnusedFilters();
        MinWritesBox.TextChanged += (_, _) => ApplyUnusedFilters();

        // Action buttons
        GenerateDropButton.Click += (_, _) => GenerateDropScript();
        GenerateCreateButton.Click += (_, _) => GenerateCreateScript();
        GenerateDropDuplicateButton.Click += (_, _) => GenerateDropDuplicateScript();

        // Export buttons
        ExportUnusedButton.Click += async (_, _) => await ExportGridAsync(UnusedGrid, "unused_indexes");
        ExportMissingButton.Click += async (_, _) => await ExportGridAsync(MissingGrid, "missing_indexes");
        ExportDuplicateButton.Click += async (_, _) => await ExportGridAsync(DuplicateGrid, "duplicate_indexes");

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Opened += (_, _) => _ = LoadDataAsync();
    }

    private async System.Threading.Tasks.Task LoadDataAsync()
    {
        if (_selectedDatabase == null) return;

        LoadingBar.IsVisible = true;
        StatusText.Text = "Loading...";

        try
        {
            var connStr = DatabaseService.BuildConnectionString(_connectionString, _selectedDatabase);

            // Load server start time
            var startTime = await _db.GetServerStartTimeAsync(connStr);
            var startText = startTime.HasValue
                ? $"Stats since last server restart: {startTime.Value:yyyy-MM-dd HH:mm:ss}"
                : "Stats since last server restart: unknown";

            // Load all three datasets
            _unusedIndexes = await _db.GetUnusedIndexesAsync(connStr, _selectedDatabase);
            _missingIndexes = await _db.GetMissingIndexesAsync(connStr, _selectedDatabase);
            _duplicateIndexes = await _db.GetDuplicateIndexesAsync(connStr, _selectedDatabase);

            ApplyUnusedFilters();
            PopulateMissingGrid();
            PopulateDuplicateGrid();

            StatusText.Text = $"{startText}  |  Unused: {_unusedIndexes.Count}  Missing: {_missingIndexes.Count}  Duplicates: {_duplicateIndexes.Count}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            LoadingBar.IsVisible = false;
        }
    }

    // ── Grid row wrapper (avoids Avalonia binding issues with dictionary keys containing dots/spaces) ──

    private static readonly string[] UnusedDisplayCols = ["Schema.Table", "Index Name", "Type", "User Seeks", "User Scans",
        "User Lookups", "User Updates", "Total Reads", "Last Read Date", "Last Write Date", "Row Count", "Size MB"];

    private static readonly string[] MissingDisplayCols = ["Schema.Table", "Equality Columns", "Inequality Columns",
        "Included Columns", "User Seeks", "User Scans", "Avg User Impact", "Last Seek Date", "Score"];

    private static readonly string[] DuplicateDisplayCols = ["Schema.Table", "Index 1 Name", "Index 1 Key Columns",
        "Index 1 Include Columns", "Index 2 Name", "Index 2 Key Columns", "Index 2 Include Columns", "Relationship"];

    /// <summary>Wraps a dictionary row for DataGrid binding via Values[i] indexer.</summary>
    private sealed class GridRow
    {
        public object?[] Values { get; }
        public Dictionary<string, object?> Source { get; }

        public GridRow(Dictionary<string, object?> source, string[] columns)
        {
            Source = source;
            Values = new object?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                Values[i] = source.TryGetValue(columns[i], out var v) ? v : null;
        }
    }

    private static void BuildGrid(DataGrid grid, string[] columns, List<Dictionary<string, object?>> data)
    {
        grid.Columns.Clear();
        grid.ItemsSource = null;

        for (var i = 0; i < columns.Length; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i],
                Binding = new Binding($"Values[{i}]")
            });
        }

        grid.ItemsSource = data.Select(r => new GridRow(r, columns)).ToList();
    }

    private static List<Dictionary<string, object?>> GetSelectedRows(DataGrid grid)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in grid.SelectedItems)
        {
            if (item is GridRow gr)
                rows.Add(gr.Source);
        }
        return rows;
    }

    // ── Unused Indexes ──────────────────────────────────────────────

    private void ApplyUnusedFilters()
    {
        var hideClustered = HideClustered.IsChecked == true;
        var hidePK = HidePrimaryKeys.IsChecked == true;
        int.TryParse(MinWritesBox.Text, out var minWrites);

        var filtered = _unusedIndexes.Where(r =>
        {
            if (hideClustered && Convert.ToInt32(r["IndexType"]) == 1) return false;
            if (hidePK && Convert.ToBoolean(r["IsPK"])) return false;
            if (Convert.ToInt64(r["User Updates"] ?? 0) < minWrites) return false;
            return true;
        }).ToList();

        BuildGrid(UnusedGrid, UnusedDisplayCols, filtered);
    }

    private void GenerateDropScript()
    {
        var selected = GetSelectedRows(UnusedGrid);
        if (selected.Count == 0) { StatusText.Text = "Select one or more rows first."; return; }

        var sb = new StringBuilder();
        foreach (var row in selected)
        {
            var table = row["Schema.Table"] as string;
            var idx = row["Index Name"] as string;
            sb.AppendLine($"DROP INDEX [{idx}] ON [{table}];");
        }

        OnScriptGenerated?.Invoke(sb.ToString());
        StatusText.Text = $"Generated DROP script for {selected.Count} index(es).";
    }

    // ── Missing Indexes ─────────────────────────────────────────────

    private void PopulateMissingGrid()
    {
        BuildGrid(MissingGrid, MissingDisplayCols, _missingIndexes);
    }

    private void GenerateCreateScript()
    {
        var selected = GetSelectedRows(MissingGrid);
        if (selected.Count == 0) { StatusText.Text = "Select one or more rows first."; return; }

        var sb = new StringBuilder();
        foreach (var row in selected)
        {
            var statement = row["Schema.Table"] as string ?? "";
            // statement is like [db].[schema].[table] — extract schema.table
            var parts = statement.Split('.').Select(p => p.Trim('[', ']')).ToArray();
            var schema = parts.Length >= 2 ? parts[^2] : "dbo";
            var table = parts.Length >= 1 ? parts[^1] : "Unknown";

            var eqCols = row["Equality Columns"] as string;
            var ineqCols = row["Inequality Columns"] as string;
            var incCols = row["Included Columns"] as string;

            var keyCols = new List<string>();
            if (!string.IsNullOrEmpty(eqCols)) keyCols.AddRange(eqCols.Split(',').Select(c => c.Trim()));
            if (!string.IsNullOrEmpty(ineqCols)) keyCols.AddRange(ineqCols.Split(',').Select(c => c.Trim()));

            var keyStr = string.Join(", ", keyCols.Select(c => $"[{c.Trim('[', ']')}]"));
            var nameBase = string.Join("_", keyCols.Take(3).Select(c => c.Trim('[', ']', ' ')));
            var indexName = $"IX_{table}_{nameBase}";

            sb.Append($"CREATE NONCLUSTERED INDEX [{indexName}] ON [{schema}].[{table}] ({keyStr})");

            if (!string.IsNullOrEmpty(incCols))
            {
                var incList = incCols.Split(',').Select(c => $"[{c.Trim().Trim('[', ']')}]");
                sb.Append($" INCLUDE ({string.Join(", ", incList)})");
            }
            sb.AppendLine(";");
            sb.AppendLine();
        }

        OnScriptGenerated?.Invoke(sb.ToString());
        StatusText.Text = $"Generated CREATE script for {selected.Count} index(es).";
    }

    // ── Duplicate Indexes ───────────────────────────────────────────

    private void PopulateDuplicateGrid()
    {
        BuildGrid(DuplicateGrid, DuplicateDisplayCols, _duplicateIndexes);
    }

    private void GenerateDropDuplicateScript()
    {
        var selected = GetSelectedRows(DuplicateGrid);
        if (selected.Count == 0) { StatusText.Text = "Select one or more rows first."; return; }

        var sb = new StringBuilder();
        foreach (var row in selected)
        {
            var table = row["Schema.Table"] as string;
            var relationship = row["Relationship"] as string ?? "";

            // Drop the smaller/redundant index
            string? indexToDrop;
            if (relationship.Contains("Index 1 is subset"))
                indexToDrop = row["Index 1 Name"] as string;
            else if (relationship.Contains("Index 2 is subset"))
                indexToDrop = row["Index 2 Name"] as string;
            else
                indexToDrop = row["Index 1 Name"] as string; // For duplicates, either one works

            sb.AppendLine($"DROP INDEX [{indexToDrop}] ON [{table}];");
        }

        OnScriptGenerated?.Invoke(sb.ToString());
        StatusText.Text = $"Generated DROP script for {selected.Count} duplicate index(es).";
    }

    // ── Export ───────────────────────────────────────────────────────

    /// <summary>Fired when a script is generated — caller opens it in a new query tab.</summary>
    public event Action<string>? OnScriptGenerated;

    private async System.Threading.Tasks.Task ExportGridAsync(DataGrid grid, string suggestedName)
    {
        if (grid.ItemsSource is not IEnumerable<GridRow> items) return;
        var data = items.ToList();
        if (data.Count == 0) { StatusText.Text = "No data to export."; return; }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to CSV",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });

        var path = file?.TryGetLocalPath();
        if (path == null) return;

        try
        {
            var columns = grid.Columns.Select(c => c.Header?.ToString() ?? "").ToList();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", columns.Select(EscapeCsv)));

            foreach (var row in data)
            {
                var values = row.Values.Select(v => v?.ToString() ?? "");
                sb.AppendLine(string.Join(",", values.Select(EscapeCsv)));
            }

            await File.WriteAllTextAsync(path, sb.ToString());
            StatusText.Text = $"Exported {data.Count} rows to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
