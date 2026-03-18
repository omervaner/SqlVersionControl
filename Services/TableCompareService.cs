using SqlVersionControl.Models;

namespace SqlVersionControl.Services;

/// <summary>
/// Compares table structures between source and target databases.
/// Pure logic — no SQL connections. Takes column lists, produces comparison results and DDL.
/// </summary>
public static class TableCompareService
{
    /// <summary>
    /// Compares all tables between source and target, returning per-table results
    /// with column-level diffs. Statuses are final (no intermediate "Both" state).
    /// </summary>
    public static List<TableCompareResult> Compare(
        List<TableColumnInfo> sourceColumns,
        List<TableColumnInfo> targetColumns)
    {
        var sourceByTable = sourceColumns.GroupBy(c => c.TableKey)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var targetByTable = targetColumns.GroupBy(c => c.TableKey)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var allTableKeys = sourceByTable.Keys.Union(targetByTable.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        var results = new List<TableCompareResult>();

        foreach (var tableKey in allTableKeys)
        {
            var inSource = sourceByTable.TryGetValue(tableKey, out var srcCols);
            var inTarget = targetByTable.TryGetValue(tableKey, out var tgtCols);

            var parts = tableKey.Split('.', 2);
            var schema = parts[0];
            var tableName = parts.Length > 1 ? parts[1] : parts[0];

            if (inSource && !inTarget)
            {
                results.Add(new TableCompareResult
                {
                    Schema = schema,
                    TableName = tableName,
                    Status = TableCompareStatus.SourceOnly,
                    Columns = srcCols!.Select(c => new ColumnCompareResult
                    {
                        ColumnName = c.ColumnName,
                        Source = c,
                        Target = null,
                        Status = ColumnCompareStatus.SourceOnly
                    }).ToList()
                });
            }
            else if (!inSource && inTarget)
            {
                results.Add(new TableCompareResult
                {
                    Schema = schema,
                    TableName = tableName,
                    Status = TableCompareStatus.TargetOnly,
                    Columns = tgtCols!.Select(c => new ColumnCompareResult
                    {
                        ColumnName = c.ColumnName,
                        Source = null,
                        Target = c,
                        Status = ColumnCompareStatus.TargetOnly
                    }).ToList()
                });
            }
            else
            {
                var columnResults = CompareColumns(srcCols!, tgtCols!);
                var hasDifferences = columnResults.Any(c => c.Status != ColumnCompareStatus.Match);

                results.Add(new TableCompareResult
                {
                    Schema = schema,
                    TableName = tableName,
                    Status = hasDifferences ? TableCompareStatus.Different : TableCompareStatus.Match,
                    Columns = columnResults
                });
            }
        }

        return results;
    }

    private static List<ColumnCompareResult> CompareColumns(
        List<TableColumnInfo> sourceCols,
        List<TableColumnInfo> targetCols)
    {
        var sourceByName = sourceCols.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var targetByName = targetCols.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        var allColumnNames = sourceByName.Keys
            .Union(targetByName.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList(); // preserve source order first, then target-only

        var results = new List<ColumnCompareResult>();

        foreach (var colName in allColumnNames)
        {
            var inSource = sourceByName.TryGetValue(colName, out var srcCol);
            var inTarget = targetByName.TryGetValue(colName, out var tgtCol);

            if (inSource && !inTarget)
            {
                results.Add(new ColumnCompareResult
                {
                    ColumnName = colName, Source = srcCol, Target = null,
                    Status = ColumnCompareStatus.SourceOnly
                });
            }
            else if (!inSource && inTarget)
            {
                results.Add(new ColumnCompareResult
                {
                    ColumnName = colName, Source = null, Target = tgtCol,
                    Status = ColumnCompareStatus.TargetOnly
                });
            }
            else
            {
                var match = ColumnsMatch(srcCol!, tgtCol!);
                results.Add(new ColumnCompareResult
                {
                    ColumnName = colName, Source = srcCol, Target = tgtCol,
                    Status = match ? ColumnCompareStatus.Match : ColumnCompareStatus.Different
                });
            }
        }

        return results;
    }

    private static bool ColumnsMatch(TableColumnInfo src, TableColumnInfo tgt)
    {
        return string.Equals(src.DataType, tgt.DataType, StringComparison.OrdinalIgnoreCase)
            && src.MaxLength == tgt.MaxLength
            && src.Precision == tgt.Precision
            && src.Scale == tgt.Scale
            && src.IsNullable == tgt.IsNullable
            && string.Equals(src.DefaultDefinition ?? "", tgt.DefaultDefinition ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ── DDL Generation ───────────────────────────────────────────────

    /// <summary>
    /// Generates a CREATE TABLE statement for a table that exists only in source.
    /// </summary>
    public static string GenerateCreateTableDdl(string schema, string tableName, List<ColumnCompareResult> columns)
    {
        var safeSchema = schema.Replace("]", "]]");
        var safeName = tableName.Replace("]", "]]");

        var lines = new List<string>();
        foreach (var col in columns.Where(c => c.Source != null))
        {
            var src = col.Source!;
            var line = $"    [{src.ColumnName.Replace("]", "]]")}] {src.FormattedType} {(src.IsNullable ? "NULL" : "NOT NULL")}";
            if (!string.IsNullOrEmpty(src.DefaultDefinition))
            {
                var constraintName = src.DefaultConstraintName ?? $"DF_{tableName}_{src.ColumnName}";
                line += $" CONSTRAINT [{constraintName.Replace("]", "]]")}] DEFAULT {src.DefaultDefinition}";
            }
            lines.Add(line);
        }

        return $"CREATE TABLE [{safeSchema}].[{safeName}]\n(\n{string.Join(",\n", lines)}\n);";
    }

    /// <summary>
    /// Generates ALTER TABLE statements for a table that exists in both but has column differences.
    /// Only generates DDL for SourceOnly and Different columns — never drops TargetOnly columns.
    /// </summary>
    public static List<string> GenerateAlterStatements(string schema, string tableName, List<ColumnCompareResult> columns)
    {
        var safeSchema = schema.Replace("]", "]]");
        var safeName = tableName.Replace("]", "]]");
        var tableRef = $"[{safeSchema}].[{safeName}]";
        var statements = new List<string>();

        foreach (var col in columns)
        {
            switch (col.Status)
            {
                case ColumnCompareStatus.SourceOnly:
                {
                    var src = col.Source!;
                    var safeCol = src.ColumnName.Replace("]", "]]");
                    var addSql = $"ALTER TABLE {tableRef} ADD [{safeCol}] {src.FormattedType} {(src.IsNullable ? "NULL" : "NOT NULL")}";
                    if (!string.IsNullOrEmpty(src.DefaultDefinition))
                    {
                        var constraintName = src.DefaultConstraintName ?? $"DF_{tableName}_{src.ColumnName}";
                        addSql += $" CONSTRAINT [{constraintName.Replace("]", "]]")}] DEFAULT {src.DefaultDefinition}";
                    }
                    statements.Add(addSql + ";");
                    break;
                }

                case ColumnCompareStatus.Different:
                {
                    var src = col.Source!;
                    var tgt = col.Target!;
                    var safeCol = src.ColumnName.Replace("]", "]]");

                    // If default constraint changed, drop old one first, then add new
                    var defaultChanged = !string.Equals(
                        src.DefaultDefinition ?? "", tgt.DefaultDefinition ?? "",
                        StringComparison.OrdinalIgnoreCase);

                    if (defaultChanged && !string.IsNullOrEmpty(tgt.DefaultConstraintName))
                    {
                        var oldConstraint = tgt.DefaultConstraintName.Replace("]", "]]");
                        statements.Add($"ALTER TABLE {tableRef} DROP CONSTRAINT [{oldConstraint}];");
                    }

                    // ALTER COLUMN for type/nullability changes
                    var typeOrNullChanged =
                        !string.Equals(src.DataType, tgt.DataType, StringComparison.OrdinalIgnoreCase)
                        || src.MaxLength != tgt.MaxLength
                        || src.Precision != tgt.Precision
                        || src.Scale != tgt.Scale
                        || src.IsNullable != tgt.IsNullable;

                    if (typeOrNullChanged)
                    {
                        statements.Add(
                            $"ALTER TABLE {tableRef} ALTER COLUMN [{safeCol}] {src.FormattedType} {(src.IsNullable ? "NULL" : "NOT NULL")};");
                    }

                    // Add new default if source has one
                    if (defaultChanged && !string.IsNullOrEmpty(src.DefaultDefinition))
                    {
                        var constraintName = src.DefaultConstraintName ?? $"DF_{tableName}_{src.ColumnName}";
                        statements.Add(
                            $"ALTER TABLE {tableRef} ADD CONSTRAINT [{constraintName.Replace("]", "]]")}] DEFAULT {src.DefaultDefinition} FOR [{safeCol}];");
                    }

                    break;
                }

                // TargetOnly — no DDL generated (informational only)
                // Match — no DDL needed
            }
        }

        return statements;
    }
}
