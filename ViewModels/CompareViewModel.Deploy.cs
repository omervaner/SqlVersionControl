using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class CompareViewModel
{
    [RelayCommand]
    private void CancelDeploy()
    {
        _deployCts?.Cancel();
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        if (SelectedObject == null) return;

        if (IsTableCompareMode)
        {
            await DeployTableAsync(SelectedObject);
            return;
        }

        if (string.IsNullOrEmpty(SourceCode)) return;

        if (DeployRequested != null)
        {
            var targetDesc = GetTargetDescription(SelectedTargetConnection);
            var confirmed = await DeployRequested(SelectedObject.FullName, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = "Deploying...";

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so it works whether object exists or not
            var deployScript = DatabaseService.ConvertToCreateOrAlter(SourceCode);

            using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();

            StatusMessage = $"Deployed {SelectedObject.FullName} to {SelectedTargetConnection?.Server}";

            // Refresh to show updated state
            await LoadDefinitionsAsync(SelectedObject);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Deploy2Async()
    {
        if (SelectedObject == null || string.IsNullOrEmpty(TargetCode)) return;

        if (DeployRequested != null)
        {
            var targetDesc = IsProductionConnection(SelectedTarget2Connection) ? "PRODUCTION" : SelectedTarget2Connection?.Server ?? "target2";
            var confirmed = await DeployRequested(SelectedObject.FullName, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = "Deploying to Target 2...";

        try
        {
            using var conn = new SqlConnection(_target2ConnectionString);
            await conn.OpenAsync();

            // Convert CREATE to CREATE OR ALTER so it works whether object exists or not
            var deployScript = DatabaseService.ConvertToCreateOrAlter(TargetCode);

            using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();

            StatusMessage = $"Deployed {SelectedObject.FullName} to {SelectedTarget2Connection?.Server}";

            // Refresh to show updated state
            await LoadDefinitionsAsync(SelectedObject);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy to Target 2 failed: {ex.Message}";
        }
    }

    private async Task DeployTableAsync(CompareObject obj, bool showStatus = true)
    {
        if (!obj.ExistsInSource) return;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, obj.FullName, StringComparison.OrdinalIgnoreCase));
        if (tableResult == null) return;

        // Confirmation for single-table deploy
        if (showStatus)
        {
            var targetDesc = GetTargetDescription(SelectedTargetConnection);

            if (DeployRequested != null)
            {
                var message = $"⚠️ TABLE STRUCTURE CHANGE — {obj.FullName}\n\nThis can cause data loss if columns are narrowed or removed.";
                var confirmed = await DeployRequested(message, targetDesc);
                if (!confirmed) return;
            }

            StatusMessage = $"Deploying table {obj.FullName}...";
        }

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            List<string> scripts;

            if (tableResult.Status == TableCompareStatus.SourceOnly)
            {
                scripts = [TableCompareService.GenerateCreateTableDdl(
                    tableResult.Schema, tableResult.TableName, tableResult.Columns)];
            }
            else
            {
                scripts = TableCompareService.GenerateAlterStatements(
                    tableResult.Schema, tableResult.TableName, tableResult.Columns);
            }

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var script in scripts)
                {
                    using var cmd = new SqlCommand(script, conn, transaction) { CommandTimeout = 30 };
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            if (showStatus)
            {
                StatusMessage = $"Deployed table {obj.FullName} ({scripts.Count} statement{(scripts.Count != 1 ? "s" : "")})";
                await LoadObjectsAsync();
            }
        }
        catch (Exception ex)
        {
            if (showStatus)
                StatusMessage = $"Deploy failed: {ex.Message}";
            else
                throw; // Let batch deploy catch it
        }
    }

    [RelayCommand]
    private async Task DeployColumnAsync(ColumnCompareResult col)
    {
        if (col == null || !col.IsDeployable || SelectedObject == null) return;

        var tableResult = _tableCompareResults.FirstOrDefault(t =>
            string.Equals(t.TableKey, SelectedObject.FullName, StringComparison.OrdinalIgnoreCase));
        if (tableResult == null) return;

        // Build scoped warning message
        var targetDesc = GetTargetDescription(SelectedTargetConnection);

        if (DeployRequested != null)
        {
            string message;
            if (col.Status == ColumnCompareStatus.SourceOnly)
            {
                message = $"Add column {col.ColumnName} to {SelectedObject.FullName}?\n{col.SourceType} {col.SourceNullable}";
            }
            else
            {
                var changes = new List<string>();
                if (col.SourceType != col.TargetType)
                    changes.Add($"{col.TargetType} → {col.SourceType}");
                if (col.SourceNullable != col.TargetNullable)
                    changes.Add($"{col.TargetNullable} → {col.SourceNullable}");
                if (col.SourceDefault != col.TargetDefault)
                    changes.Add($"Default: {(string.IsNullOrEmpty(col.TargetDefault) ? "(none)" : col.TargetDefault)} → {(string.IsNullOrEmpty(col.SourceDefault) ? "(none)" : col.SourceDefault)}");

                message = $"Alter column {col.ColumnName} on {SelectedObject.FullName}?\n{string.Join(", ", changes)}.\nThis may cause data truncation.";
            }

            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        StatusMessage = $"Deploying column {col.ColumnName}...";

        try
        {
            using var conn = new SqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            var scripts = col.Status == ColumnCompareStatus.SourceOnly
                ? TableCompareService.GenerateAlterStatements(tableResult.Schema, tableResult.TableName, [col])
                : TableCompareService.GenerateAlterStatements(tableResult.Schema, tableResult.TableName, [col]);

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var script in scripts)
                {
                    using var cmd = new SqlCommand(script, conn, transaction) { CommandTimeout = 30 };
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            // Update column status in-place (observable — UI updates automatically)
            col.Status = ColumnCompareStatus.Match;

            // Update parent table status if all columns now match
            if (tableResult.Columns.All(c => c.Status == ColumnCompareStatus.Match))
            {
                tableResult.Status = TableCompareStatus.Match;
                SelectedObject.Status = "Identical"; // setter notifies DisplayName + StatusIcon

                // Update summary counts
                ModifiedCount = _tableCompareResults.Count(t => t.Status == TableCompareStatus.Different);
                IdenticalCount = _tableCompareResults.Count(t => t.Status == TableCompareStatus.Match);
                OnPropertyChanged(nameof(HasSummary));
            }

            StatusMessage = $"Deployed column {col.ColumnName} on {SelectedObject.FullName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Deploy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var obj in Objects.Where(o => o.ExistsInSource))
        {
            obj.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var obj in Objects)
        {
            obj.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task DeploySelectedAsync()
    {
        var selectedObjects = Objects.Where(o => o.IsSelected && o.ExistsInSource).ToList();
        if (selectedObjects.Count == 0) return;

        var targetDesc = GetTargetDescription(SelectedTargetConnection);

        if (DeployRequested != null)
        {
            var label = IsTableCompareMode ? "tables" : "objects";
            var objectNames = string.Join(", ", selectedObjects.Take(3).Select(o => o.ObjectName));
            if (selectedObjects.Count > 3) objectNames += $" (+{selectedObjects.Count - 3} more)";

            var message = IsTableCompareMode
                ? $"⚠️ TABLE STRUCTURE CHANGE — {selectedObjects.Count} {label}: {objectNames}\n\nThis can cause data loss if columns are narrowed or removed."
                : $"{selectedObjects.Count} {label}: {objectNames}";

            var confirmed = await DeployRequested(message, targetDesc);
            if (!confirmed) return;
        }

        _deployCts?.Cancel();
        _deployCts?.Dispose();
        _deployCts = new CancellationTokenSource();
        var token = _deployCts.Token;

        IsDeploying = true;
        var total = selectedObjects.Count;
        var successCount = 0;
        var failCount = 0;
        var failures = new List<(string ObjectName, string Error)>();

        try
        {
            for (int i = 0; i < selectedObjects.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var obj = selectedObjects[i];
                StatusMessage = $"Deploying {i + 1}/{total}: {obj.ObjectName}...";

                try
                {
                    if (IsTableCompareMode)
                    {
                        await DeployTableAsync(obj, showStatus: false);
                    }
                    else
                    {
                        // Get definition if not cached
                        var sourceCode = obj.SourceDefinition;
                        if (string.IsNullOrEmpty(sourceCode))
                        {
                            sourceCode = await GetDefinitionAsync(_sourceConnectionString, obj.SchemaName, obj.ObjectName);
                        }

                        var deployScript = DatabaseService.ConvertToCreateOrAlter(sourceCode);

                        using var conn = new SqlConnection(_targetConnectionString);
                        await conn.OpenAsync(token);
                        using var cmd = new SqlCommand(deployScript, conn) { CommandTimeout = 30 };
                        await cmd.ExecuteNonQueryAsync(token);
                    }

                    obj.IsSelected = false;
                    successCount++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add((obj.ObjectName, ex.Message));
                    failCount++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Deploy cancelled — {successCount} succeeded, {failCount} failed, {total - successCount - failCount} skipped";
        }

        IsDeploying = false;
        UpdateSelectedCount();

        if (!token.IsCancellationRequested)
        {
            if (failCount == 0)
                StatusMessage = $"Successfully deployed {successCount} {(IsTableCompareMode ? "tables" : "objects")} to {targetDesc}";
            else
            {
                var failDetails = string.Join("; ", failures.Take(3).Select(f => $"{f.ObjectName}: {f.Error}"));
                if (failures.Count > 3) failDetails += $" (+{failures.Count - 3} more)";
                StatusMessage = $"Deployed {successCount}, {failCount} failed — {failDetails}";
            }
        }

        // Refresh to update states
        await LoadObjectsAsync();
    }
}
