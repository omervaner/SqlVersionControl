using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class CompareViewModel
{
    private async Task LoadObjectsAsync()
    {
        if (!IsSourceConnected && !IsTargetConnected) return;

        // Remember selected object to restore after refresh
        var selectedFullName = SelectedObject?.FullName;

        _allObjects.Clear();
        _tableCompareResults.Clear();

        if (IsTableCompareMode)
        {
            await LoadTableObjectsAsync();
        }
        else
        {
            await LoadCodeObjectsAsync();
        }

        FilterObjects();

        // Re-select the same object (new instance) and reload definitions
        if (!string.IsNullOrEmpty(selectedFullName))
        {
            var matchingObject = Objects.FirstOrDefault(o => o.FullName == selectedFullName);
            if (matchingObject != null)
            {
                SelectedObject = matchingObject;
                if (IsTableCompareMode)
                    LoadTableColumns(matchingObject);
                else
                    await LoadDefinitionsAsync(matchingObject);
            }
            else
            {
                SelectedObject = null;
            }
        }
    }

    private async Task LoadCodeObjectsAsync()
    {
        var sourceObjects = new Dictionary<string, string>();
        var targetObjects = new Dictionary<string, string>();

        if (IsSourceConnected)
            sourceObjects = await GetObjectsFromDatabaseAsync(_sourceConnectionString);
        if (IsTargetConnected)
            targetObjects = await GetObjectsFromDatabaseAsync(_targetConnectionString);

        var allKeys = sourceObjects.Keys.Union(targetObjects.Keys).OrderBy(k => k);

        foreach (var key in allKeys)
        {
            var parts = key.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "dbo";
            var name = parts.Length > 1 ? parts[1] : parts[0];

            var existsInSource = sourceObjects.ContainsKey(key);
            var existsInTarget = targetObjects.ContainsKey(key);

            _allObjects.Add(new CompareObject
            {
                SchemaName = schema,
                ObjectName = name,
                FullName = key,
                ExistsInSource = existsInSource,
                ExistsInTarget = existsInTarget,
                Status = GetCompareStatus(existsInSource, existsInTarget)
            });
        }
    }

    private async Task LoadTableObjectsAsync()
    {
        var sourceColumns = new List<TableColumnInfo>();
        var targetColumns = new List<TableColumnInfo>();

        try
        {
            if (IsSourceConnected)
            {
                var sourceDb = SelectedSourceConnection!.Database;
                sourceColumns = await DatabaseService.GetTableStructureAsync(_sourceConnectionString, sourceDb);
            }
            if (IsTargetConnected)
            {
                var targetDb = SelectedTargetConnection!.Database;
                targetColumns = await DatabaseService.GetTableStructureAsync(_targetConnectionString, targetDb);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading tables: {ex.Message}";
            return;
        }

        _tableCompareResults = TableCompareService.Compare(sourceColumns, targetColumns);

        // Update summary counts immediately (no scan phase needed for tables)
        SourceOnlyCount = 0;
        TargetOnlyCount = 0;
        ModifiedCount = 0;
        IdenticalCount = 0;

        foreach (var table in _tableCompareResults)
        {
            var status = TableStatusToString(table.Status);
            var existsInSource = table.Status != TableCompareStatus.TargetOnly;
            var existsInTarget = table.Status != TableCompareStatus.SourceOnly;

            _allObjects.Add(new CompareObject
            {
                SchemaName = table.Schema,
                ObjectName = table.TableName,
                FullName = table.TableKey,
                ExistsInSource = existsInSource,
                ExistsInTarget = existsInTarget,
                Status = status,
                HasBeenCompared = true // statuses are final
            });

            switch (table.Status)
            {
                case TableCompareStatus.SourceOnly: SourceOnlyCount++; break;
                case TableCompareStatus.TargetOnly: TargetOnlyCount++; break;
                case TableCompareStatus.Different: ModifiedCount++; break;
                case TableCompareStatus.Match: IdenticalCount++; break;
            }
        }

        OnPropertyChanged(nameof(HasSummary));
    }

    private static string TableStatusToString(TableCompareStatus status) => status switch
    {
        TableCompareStatus.Match => "Identical",
        TableCompareStatus.Different => "Modified",
        TableCompareStatus.SourceOnly => "Source Only",
        TableCompareStatus.TargetOnly => "Target Only",
        _ => "?"
    };

    private void LoadTableColumns(CompareObject obj)
    {
        TableCompareColumns.Clear();
        SourceCode = "";
        TargetCode = "";
        DiffModel = null;
        CanDeploy = false;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, obj.FullName, StringComparison.OrdinalIgnoreCase));

        if (tableResult == null) return;

        foreach (var col in tableResult.Columns)
            TableCompareColumns.Add(col);

        // Can deploy if source has this table and target is connected
        CanDeploy = IsTargetConnected && obj.ExistsInSource;
    }

    private async Task<Dictionary<string, string>> GetObjectsFromDatabaseAsync(string connectionString)
    {
        var objects = new Dictionary<string, string>();

        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT
                    s.name as SchemaName,
                    o.name as ObjectName,
                    o.type_desc
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'V', 'TR')
                  AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var key = $"{schema}.{name}";
                objects[key] = reader.GetString(2);
            }
        }
        catch
        {
            // Ignore errors, return empty dict
        }

        return objects;
    }

    private string GetCompareStatus(bool inSource, bool inTarget)
    {
        if (inSource && inTarget) return "Uncompared";
        if (inSource) return "Source Only";
        return "Target Only";
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterObjects();
    }

    partial void OnShowOnlyDifferencesChanged(bool value)
    {
        // In table mode, statuses are already resolved — just filter, no scan needed
        if (IsTableCompareMode)
        {
            FilterObjects();
            return;
        }

        if (value && IsSourceConnected && IsTargetConnected)
        {
            IsScanning = true;
            ScanProgressText = "Preparing scan...";
            _ = ScanForDifferencesAsync();
        }
        else
        {
            FilterObjects();
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    private async Task ScanForDifferencesAsync()
    {
        if (!IsSourceConnected || !IsTargetConnected) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanProgress = 0;
        SourceOnlyCount = 0;
        TargetOnlyCount = 0;
        ModifiedCount = 0;
        IdenticalCount = 0;

        var objectsToScan = _allObjects.Where(o => o.ExistsInSource && o.ExistsInTarget && !o.HasBeenCompared).ToList();
        var total = objectsToScan.Count;
        var current = 0;

        // First count source-only and target-only
        SourceOnlyCount = _allObjects.Count(o => o.Status == "Source Only");
        TargetOnlyCount = _allObjects.Count(o => o.Status == "Target Only");

        // Parallel scan with bounded concurrency
        var semaphore = new SemaphoreSlim(5);

        try
        {
            var tasks = objectsToScan.Select(async obj =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();

                    obj.SourceDefinition = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
                    obj.TargetDefinition = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
                    obj.HasBeenCompared = true;

                    var sourceNorm = NormalizeForComparison(obj.SourceDefinition);
                    var targetNorm = NormalizeForComparison(obj.TargetDefinition);
                    obj.Status = sourceNorm == targetNorm ? "Identical" : "Modified";

                    var c = Interlocked.Increment(ref current);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ScanProgressText = $"Scanning {c}/{total}: {obj.ObjectName}";
                        ScanProgress = (double)c / total * 100;
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Scan cancelled ({current}/{total} objects compared)";
        }

        // Count results from whatever was compared (full or partial)
        ModifiedCount = 0;
        IdenticalCount = 0;
        foreach (var obj in _allObjects.Where(o => o.HasBeenCompared))
        {
            if (obj.Status == "Identical")
                IdenticalCount++;
            else if (obj.Status == "Modified")
                ModifiedCount++;
        }

        IsScanning = false;
        ScanProgressText = "";
        OnPropertyChanged(nameof(HasSummary));
        FilterObjects();

        if (!token.IsCancellationRequested)
            StatusMessage = $"Scan complete: {ModifiedCount} modified, {SourceOnlyCount} source only, {TargetOnlyCount} target only";
    }

    private string NormalizeForComparison(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        // Normalize line endings and trim whitespace from each line
        return string.Join("\n", code.Split('\n').Select(l => l.Trim()));
    }

    private void FilterObjects()
    {
        Objects.Clear();

        IEnumerable<CompareObject> filtered = _allObjects;

        // Apply "show only differences" filter
        if (ShowOnlyDifferences)
        {
            filtered = filtered.Where(o =>
                o.Status == "Source Only" ||
                o.Status == "Target Only" ||
                o.Status == "Modified");
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            // Normalize search text: replace underscores with spaces, then split
            var normalizedSearch = SearchText.Replace("_", " ");
            var searchTerms = normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = filtered.Where(o =>
            {
                var name = o.ObjectName.Replace("_", " ");
                var schema = o.SchemaName.Replace("_", " ");
                return searchTerms.All(term =>
                    name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    schema.Contains(term, StringComparison.OrdinalIgnoreCase));
            });
        }

        foreach (var o in filtered)
        {
            Objects.Add(o);
        }

        OnPropertyChanged(nameof(HasObjects));
        UpdateStatusMessage();
    }

    partial void OnSelectedObjectChanged(CompareObject? value)
    {
        if (value != null)
        {
            if (IsDataCompareMode)
            {
                // Auto-compare data when selecting a table in data mode
                _ = CompareDataAsync();
                return;
            }
            if (IsTableCompareMode)
            {
                LoadTableColumns(value);
                return;
            }
            _ = LoadDefinitionsAsync(value);
        }
    }

    private async Task LoadDefinitionsAsync(CompareObject obj)
    {
        SourceCode = "";
        TargetCode = "";
        Target2Code = "";
        CanDeploy = false;
        CanDeploy2 = false;

        if (IsSourceConnected && obj.ExistsInSource)
        {
            SourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
        }

        if (IsTargetConnected && obj.ExistsInTarget)
        {
            TargetCode = await GetDefinitionAsync(_targetConnectionString, obj.SchemaName, obj.ObjectName);
        }

        // Load Target2 definition if connected
        if (IsTarget2Connected)
        {
            Target2Code = await GetDefinitionAsync(_target2ConnectionString, obj.SchemaName, obj.ObjectName);
        }

        UpdateDiff();
        UpdateDiff2();

        // Can deploy if source has code and target is connected
        CanDeploy = IsTargetConnected && !string.IsNullOrEmpty(SourceCode);

        // Can deploy to Target2 if Target1 has code and Target2 is connected
        CanDeploy2 = IsTarget2Connected && !string.IsNullOrEmpty(TargetCode);
    }

    private async Task<string> GetDefinitionAsync(string connectionString, string schema, string objectName)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT m.definition
                FROM sys.sql_modules m
                JOIN sys.objects o ON m.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @schema AND o.name = @name";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@name", objectName);

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "(Definition not available)";
        }
        catch (Exception ex)
        {
            return $"-- Error: {ex.Message}";
        }
    }

    private void UpdateDiff()
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        DiffModel = diffBuilder.BuildDiffModel(SourceCode, TargetCode);
    }

    private void UpdateDiff2()
    {
        if (!IsTarget2Connected || string.IsNullOrEmpty(TargetCode))
        {
            DiffModel2 = null;
            return;
        }

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        DiffModel2 = diffBuilder.BuildDiffModel(TargetCode, Target2Code);
    }
}
