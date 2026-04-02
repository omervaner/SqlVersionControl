using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class CompareViewModel
{
    partial void OnIsDataCompareModeChanged(bool value)
    {
        if (value)
        {
            // Data mode needs table list — enable table mode if not already
            if (!IsTableCompareMode)
                IsTableCompareMode = true;

            // If a table is already selected, auto-compare it
            if (SelectedObject != null && IsSourceConnected && IsTargetConnected)
                _ = CompareDataAsync();
        }
        else
        {
            // Leaving data mode — clear data compare state
            DataCompareResult = null;
            DataCompareRows.Clear();
            FilteredDataRows.Clear();
            SelectedRowFields.Clear();
            DataCompareSummary = "";
        }
    }

    [RelayCommand]
    private void ToggleDataCompareMode()
    {
        IsDataCompareMode = !IsDataCompareMode;
    }

    [RelayCommand]
    private async Task CompareDataAsync()
    {
        if (SelectedObject == null ||
            string.IsNullOrEmpty(_sourceConnectionString) ||
            string.IsNullOrEmpty(_targetConnectionString))
        {
            StatusMessage = "Select a table and connect both source and target first.";
            return;
        }

        IsDataCompareMode = true;
        IsDataLoading = true;
        StatusMessage = $"Comparing data for {SelectedObject.FullName}...";
        DataCompareRows.Clear();
        FilteredDataRows.Clear();
        SelectedRowFields.Clear();
        DataCompareSummary = "";
        SelectedDataRow = null;

        try
        {
            var service = new DataCompareService();
            var parts = SelectedObject.FullName.Split('.', 2);
            var schema = parts[0];
            var table = parts.Length > 1 ? parts[1] : parts[0];

            var result = await service.CompareDataAsync(
                _sourceConnectionString, _targetConnectionString, schema, table);

            DataCompareResult = result;

            // Populate column filter dropdown
            DataFilterColumns.Clear();
            DataFilterColumns.Add(""); // "All" option
            foreach (var col in result.ColumnNames)
                DataFilterColumns.Add(col);
            DataFilterColumn = "";
            DataFilterText = "";

            // Populate rows
            foreach (var row in result.Rows)
                DataCompareRows.Add(row);

            ApplyDataFilter();
            DataCompareSummary = result.Summary;
            StatusMessage = result.Summary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Data compare failed: {ex.Message}";
            DataCompareSummary = "";
        }
        finally
        {
            IsDataLoading = false;
        }
    }

    partial void OnDataFilterColumnChanged(string value) => ApplyDataFilter();
    partial void OnDataFilterTextChanged(string value) => ApplyDataFilter();

    private void ApplyDataFilter()
    {
        FilteredDataRows.Clear();

        if (DataCompareResult == null) return;

        var filterText = DataFilterText?.Trim() ?? "";
        var filterColName = DataFilterColumn ?? "";

        int filterColIndex = -1;
        if (!string.IsNullOrEmpty(filterColName))
            filterColIndex = Array.FindIndex(DataCompareResult.ColumnNames,
                c => c.Equals(filterColName, StringComparison.OrdinalIgnoreCase));

        foreach (var row in DataCompareRows)
        {
            if (string.IsNullOrEmpty(filterText))
            {
                FilteredDataRows.Add(row);
                continue;
            }

            // Filter: check if the specified column (or any column) contains the text
            if (filterColIndex >= 0)
            {
                var val = row.GetDisplayValue(filterColIndex)?.ToString() ?? "";
                if (val.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    FilteredDataRows.Add(row);
            }
            else
            {
                // Search all columns
                var values = row.Status == DataRowStatus.TargetOnly ? row.TargetValues : row.SourceValues;
                if (values.Any(v => v?.ToString()?.Contains(filterText, StringComparison.OrdinalIgnoreCase) == true))
                    FilteredDataRows.Add(row);
            }
        }
    }

    partial void OnSelectedDataRowChanged(DataCompareRow? value)
    {
        SelectedRowFields.Clear();
        if (value == null || DataCompareResult == null) return;

        var cols = DataCompareResult.ColumnNames;
        var pkSet = new HashSet<int>(DataCompareResult.PkColumnIndices);

        for (int i = 0; i < cols.Length; i++)
        {
            var srcVal = value.SourceValues.ElementAtOrDefault(i);
            var tgtVal = value.TargetValues.ElementAtOrDefault(i);
            var isDiff = value.DifferentColumnIndices.Contains(i);

            string srcDisplay, tgtDisplay;
            bool srcNull = false, tgtNull = false;

            if (value.Status == DataRowStatus.SourceOnly)
            {
                srcDisplay = srcVal == null ? "NULL" : srcVal.ToString()!;
                tgtDisplay = "—";
                srcNull = srcVal == null;
            }
            else if (value.Status == DataRowStatus.TargetOnly)
            {
                srcDisplay = "—";
                tgtDisplay = tgtVal == null ? "NULL" : tgtVal.ToString()!;
                tgtNull = tgtVal == null;
            }
            else
            {
                srcDisplay = srcVal == null ? "NULL" : srcVal.ToString()!;
                tgtDisplay = tgtVal == null ? "NULL" : tgtVal.ToString()!;
                srcNull = srcVal == null;
                tgtNull = tgtVal == null;
            }

            SelectedRowFields.Add(new DataCompareField
            {
                ColumnName = cols[i],
                SourceDisplay = srcDisplay,
                TargetDisplay = tgtDisplay,
                IsDifferent = isDiff,
                IsPrimaryKey = pkSet.Contains(i),
                IsSourceNull = srcNull,
                IsTargetNull = tgtNull
            });
        }
    }

    [RelayCommand]
    private void ShowDataDeploySql()
    {
        if (DataCompareResult == null) return;

        var rowsToDeploy = DataCompareRows.Where(r => r.IsSelected && r.Status != DataRowStatus.Identical).ToList();
        if (rowsToDeploy.Count == 0)
        {
            StatusMessage = "Select rows to deploy first (use checkboxes).";
            return;
        }

        var sql = DataCompareService.GenerateDeploySql(DataCompareResult, rowsToDeploy);
        // Store in SourceCode so the UI can display it (reuse existing code panel)
        SourceCode = sql;
        StatusMessage = $"Generated SQL for {rowsToDeploy.Count} row(s)";
    }

    [RelayCommand]
    private async Task DeployDataRowsAsync()
    {
        if (DataCompareResult == null) return;

        var rowsToDeploy = DataCompareRows.Where(r => r.IsSelected && r.Status != DataRowStatus.Identical).ToList();
        if (rowsToDeploy.Count == 0)
        {
            StatusMessage = "Select rows to deploy first (use checkboxes).";
            return;
        }

        // Confirmation
        var targetDesc = GetTargetDescription(SelectedTargetConnection);

        if (DeployRequested != null)
        {
            var message = $"Deploy {rowsToDeploy.Count} row(s) to {DataCompareResult.Schema}.{DataCompareResult.TableName} on {targetDesc}?";
            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = $"Deploying {rowsToDeploy.Count} row(s)...";

        try
        {
            var service = new DataCompareService();
            var (success, msg) = await service.DeployRowsAsync(_targetConnectionString, DataCompareResult, rowsToDeploy);

            if (success)
            {
                StatusMessage = msg;
                // Refresh data comparison
                await CompareDataAsync();
            }
            else
            {
                StatusMessage = msg;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeployFieldAsync(DataCompareField field)
    {
        if (field == null || !field.IsDifferent || DataCompareResult == null || SelectedDataRow == null)
            return;

        var colIndex = Array.FindIndex(DataCompareResult.ColumnNames,
            c => c.Equals(field.ColumnName, StringComparison.OrdinalIgnoreCase));
        if (colIndex < 0) return;

        var pkIndices = DataCompareResult.PkColumnIndices;
        var cols = DataCompareResult.ColumnNames;
        var row = SelectedDataRow;

        // Build a single UPDATE for this field
        var schema = DataCompareResult.Schema.Replace("]", "]]");
        var table = DataCompareResult.TableName.Replace("]", "]]");
        var tableRef = $"[{schema}].[{table}]";

        var targetDesc = GetTargetDescription(SelectedTargetConnection);

        if (DeployRequested != null)
        {
            var srcVal = row.SourceValues[colIndex]?.ToString() ?? "NULL";
            var tgtVal = row.TargetValues[colIndex]?.ToString() ?? "NULL";
            var message = $"Update {field.ColumnName} from '{tgtVal}' to '{srcVal}' on {DataCompareResult.Schema}.{DataCompareResult.TableName}?";
            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            var setClauses = $"[{cols[colIndex]}] = @setVal";
            var whereClauses = pkIndices.Select(i => $"[{cols[i]}] = @pk_{i}");
            var sql = $"UPDATE {tableRef} SET {setClauses} WHERE {string.Join(" AND ", whereClauses)}";

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@setVal", row.SourceValues[colIndex] ?? DBNull.Value);
            foreach (var i in pkIndices)
                cmd.Parameters.AddWithValue($"@pk_{i}", row.SourceValues[i] ?? DBNull.Value);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 1)
            {
                StatusMessage = $"Deployed {field.ColumnName}";
                await CompareDataAsync();
            }
            else
            {
                StatusMessage = $"UPDATE affected {affected} rows (expected 1)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAllDataRows()
    {
        foreach (var row in FilteredDataRows)
            if (row.Status != DataRowStatus.Identical)
                row.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllDataRows()
    {
        foreach (var row in DataCompareRows)
            row.IsSelected = false;
    }
}
