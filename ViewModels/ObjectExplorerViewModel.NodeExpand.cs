using System.Collections.ObjectModel;
using Avalonia.Threading;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ObjectExplorerViewModel
{
    // ── Node wiring & expand ─────────────────────────────────────────

    private ObjectExplorerNode WireNode(ObjectExplorerNode node)
    {
        node.ExpandRequested += n => _ = OnNodeExpandedAsync(n);
        return node;
    }

    /// <summary>
    /// Propagate ConnectionId to a child node from its parent.
    /// </summary>
    private ObjectExplorerNode WireChild(ObjectExplorerNode child, ObjectExplorerNode parent)
    {
        child.ConnectionId = parent.ConnectionId;
        return WireNode(child);
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
                case ObjectExplorerNodeType.Connection:
                    await LoadConnectionChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Database:
                    await LoadDatabaseChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Folder:
                    await LoadFolderChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Table:
                    await LoadTableChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.View:
                    await LoadViewChildrenAsync(node);
                    break;
                case ObjectExplorerNodeType.Proc:
                case ObjectExplorerNodeType.Function:
                    await LoadProcChildrenAsync(node);
                    break;
            }
        }
        catch (Exception ex)
        {
            node.Children.Add(new ObjectExplorerNode
            {
                Name = $"Error: {ex.Message}",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = node.ConnectionId
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

    // ── Connection node expand → databases ───────────────────────────

    private async Task LoadConnectionChildrenAsync(ObjectExplorerNode connNode)
    {
        var connStr = ResolveConnectionString(connNode);
        if (connStr == null)
        {
            connNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(not connected)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = connNode.ConnectionId
            });
            return;
        }

        var databases = await _db.GetDatabasesAsync(connStr);
        foreach (var dbName in databases)
        {
            connNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = dbName,
                DatabaseName = dbName,
                NodeType = ObjectExplorerNodeType.Database,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, connNode));
        }

        if (connNode.Children.Count == 0)
        {
            connNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(no databases)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = connNode.ConnectionId
            });
        }
    }

    // ── Database node expand → folders ───────────────────────────────

    private Task LoadDatabaseChildrenAsync(ObjectExplorerNode dbNode)
    {
        var folders = new[]
        {
            ("Tables", ObjectExplorerNodeType.Folder),
            ("Views", ObjectExplorerNodeType.Folder),
            ("Stored Procedures", ObjectExplorerNodeType.Folder),
            ("Functions", ObjectExplorerNodeType.Folder),
            ("Sequences", ObjectExplorerNodeType.Folder),
            ("Types", ObjectExplorerNodeType.Folder),
            ("Database Triggers", ObjectExplorerNodeType.Folder),
            ("Jobs", ObjectExplorerNodeType.Folder),
        };

        foreach (var (name, type) in folders)
        {
            dbNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = dbNode.DatabaseName,
                NodeType = type,
                IsCategoryFolder = true,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, dbNode));
        }

        // Fire-and-forget: fetch object counts for all category folders
        _ = Task.Run(async () =>
        {
            try
            {
                var connStr = ResolveConnectionString(dbNode);
                if (connStr == null) return;
                var counts = await _db.GetObjectCountsAsync(connStr, dbNode.DatabaseName);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var folder in dbNode.Children)
                    {
                        if (counts.TryGetValue(folder.Name, out var count))
                            folder.ChildCount = count;
                    }
                });
            }
            catch (Exception ex) { AppLogger.LogError("ObjectExplorer.LoadCounts", ex); }
        });

        return Task.CompletedTask;
    }

    // ── Folder node expand → objects ─────────────────────────────────

    private async Task LoadFolderChildrenAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        switch (folderNode.Name)
        {
            case "Tables":
                var tables = connStr != null
                    ? await _db.GetTablesAsync(connStr, db)
                    : await _db.GetTablesAsync(db);
                var tableNodes = tables.Select(t => WireChild(new ObjectExplorerNode
                {
                    Name = t.Name, Schema = t.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Table,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }, folderNode)).ToList();
                await AddChildrenInBatchesAsync(folderNode, tableNodes);

                // Fire-and-forget: fetch row counts from metadata (instant, no table scans)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var effectiveConn = connStr ?? _activeConnectionString;
                        if (effectiveConn == null) return;
                        var counts = await _db.GetTableRowCountsAsync(effectiveConn, db);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            foreach (var node in tableNodes)
                            {
                                var key = $"{(string.IsNullOrEmpty(node.Schema) ? "dbo" : node.Schema)}.{node.Name}";
                                if (counts.TryGetValue(key, out var count))
                                    node.RowCount = count;
                            }
                        });
                    }
                    catch (Exception ex) { AppLogger.Log($"Row count fetch failed: {ex.Message}"); }
                });
                break;

            case "Views":
                var views = connStr != null
                    ? await _db.GetViewsAsync(connStr, db)
                    : await _db.GetViewsAsync(db);
                var viewNodes = views.Select(v => WireChild(new ObjectExplorerNode
                {
                    Name = v.Name, Schema = v.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.View,
                    Children = [ObjectExplorerNode.CreateDummy()]
                }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, viewNodes);
                break;

            case "Stored Procedures":
                var procsAndFuncs = connStr != null
                    ? await _db.GetProcsAndFunctionsAsync(connStr, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var procNodes = procsAndFuncs
                    .Where(x => x.Type == "SQL_STORED_PROCEDURE")
                    .Select(p => WireChild(new ObjectExplorerNode
                    {
                        Name = p.Name, Schema = p.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Proc,
                        Children = [ObjectExplorerNode.CreateDummy()]
                    }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, procNodes);
                break;

            case "Functions":
                var funcs = connStr != null
                    ? await _db.GetProcsAndFunctionsAsync(connStr, db)
                    : await _db.GetProcsAndFunctionsAsync(db);
                var funcNodes = funcs
                    .Where(x => x.Type != "SQL_STORED_PROCEDURE")
                    .Select(f => WireChild(new ObjectExplorerNode
                    {
                        Name = f.Name, Schema = f.Schema, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Function,
                        Children = [ObjectExplorerNode.CreateDummy()]
                    }, folderNode));
                await AddChildrenInBatchesAsync(folderNode, funcNodes);
                break;

            case "Sequences":
                var sequences = connStr != null
                    ? await _db.GetSequencesAsync(connStr, db)
                    : await _db.GetSequencesAsync(db);
                var seqNodes = sequences.Select(seq => new ObjectExplorerNode
                {
                    Name = seq.Name, Schema = seq.Schema, DatabaseName = db,
                    NodeType = ObjectExplorerNodeType.Sequence,
                    TypeInfo = $"{seq.DataType}, Current: {seq.CurrentValue}",
                    ConnectionId = folderNode.ConnectionId
                });
                await AddChildrenInBatchesAsync(folderNode, seqNodes);
                break;

            case "Columns":
                await LoadColumnsAsync(folderNode);
                break;

            case "Parameters":
                await LoadParametersAsync(folderNode);
                break;

            case "Indexes":
                await LoadIndexesAsync(folderNode);
                break;

            case "Keys":
                await LoadForeignKeysAsync(folderNode);
                break;

            case "Constraints":
                await LoadConstraintsAsync(folderNode);
                break;

            case "Triggers":
                await LoadTableTriggersAsync(folderNode);
                break;

            case "Types":
                await LoadUserTypesAsync(folderNode);
                break;

            case "Database Triggers":
                await LoadDatabaseTriggersAsync(folderNode);
                break;

            case "Jobs":
                try
                {
                    var jobs = connStr != null
                        ? await _db.GetJobsAsync(connStr)
                        : await _db.GetJobsAsync();
                    var jobNodes = jobs.Select(j => new ObjectExplorerNode
                    {
                        Name = j.Name, DatabaseName = db,
                        NodeType = ObjectExplorerNodeType.Job,
                        TypeInfo = j.Enabled
                            ? $"Enabled, Last: {j.LastOutcome}"
                            : "Disabled",
                        ConnectionId = folderNode.ConnectionId
                    });
                    await AddChildrenInBatchesAsync(folderNode, jobNodes);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("ObjectExplorer.LoadJobs", ex);
                    folderNode.Children.Add(new ObjectExplorerNode
                    {
                        Name = $"(Error: {ex.Message})",
                        NodeType = ObjectExplorerNodeType.Folder,
                        ConnectionId = folderNode.ConnectionId
                    });
                }
                break;
        }

        if (folderNode.Children.Count == 0)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = "(empty)",
                NodeType = ObjectExplorerNodeType.Folder,
                ConnectionId = folderNode.ConnectionId
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

    // ── Table expand → Columns + Triggers folders ────────────────────

    private Task LoadTableChildrenAsync(ObjectExplorerNode tableNode)
    {
        var subfolders = new[] { "Columns", "Indexes", "Keys", "Constraints", "Triggers" };
        foreach (var name in subfolders)
        {
            tableNode.Children.Add(WireChild(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = tableNode.DatabaseName,
                Schema = tableNode.Schema,
                ParentTableName = tableNode.Name,
                NodeType = ObjectExplorerNodeType.Folder,
                Children = [ObjectExplorerNode.CreateDummy()]
            }, tableNode));
        }

        return Task.CompletedTask;
    }

    // ── View expand → Columns folder ───────────────────────────────

    private Task LoadViewChildrenAsync(ObjectExplorerNode viewNode)
    {
        var columnsFolder = WireChild(new ObjectExplorerNode
        {
            Name = "Columns",
            DatabaseName = viewNode.DatabaseName,
            Schema = viewNode.Schema,
            ParentTableName = viewNode.Name,
            NodeType = ObjectExplorerNodeType.Folder,
            Children = [ObjectExplorerNode.CreateDummy()]
        }, viewNode);
        viewNode.Children.Add(columnsFolder);

        return Task.CompletedTask;
    }

    // ── Proc/Function expand → Parameters folder ────────────────────

    private Task LoadProcChildrenAsync(ObjectExplorerNode procNode)
    {
        var paramsFolder = WireChild(new ObjectExplorerNode
        {
            Name = "Parameters",
            DatabaseName = procNode.DatabaseName,
            Schema = procNode.Schema,
            ParentTableName = procNode.Name,
            NodeType = ObjectExplorerNodeType.Folder,
            Children = [ObjectExplorerNode.CreateDummy()]
        }, procNode);
        procNode.Children.Add(paramsFolder);

        return Task.CompletedTask;
    }

    private async Task LoadParametersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var procName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var parameters = connStr != null
            ? await _db.GetProcParametersDetailedAsync(connStr, db, schema, procName)
            : await _db.GetProcParametersDetailedAsync(db, schema, procName);

        foreach (var (name, typeName, maxLength, precision, scale, isOutput) in parameters)
        {
            var typeInfo = SqlTypeFormatter.Format(typeName, maxLength, precision, scale);
            if (isOutput) typeInfo += " OUTPUT";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Parameter,
                TypeInfo = typeInfo,
                ParentTableName = procName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadIndexesAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var indexes = connStr != null
            ? await _db.GetTableIndexesAsync(connStr, db, schema, tableName)
            : await _db.GetTableIndexesAsync(db, schema, tableName);

        foreach (var (name, typeDesc, keyCols, includeCols) in indexes)
        {
            var typeInfo = typeDesc;
            if (!string.IsNullOrEmpty(keyCols))
                typeInfo += $" ({keyCols})";
            if (!string.IsNullOrEmpty(includeCols))
                typeInfo += $" INCLUDE ({includeCols})";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadForeignKeysAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var fks = connStr != null
            ? await _db.GetForeignKeysAsync(connStr, db, schema, tableName)
            : await _db.GetForeignKeysAsync(db, schema, tableName);

        foreach (var (name, refTable, colMapping, deleteAction, updateAction) in fks)
        {
            var typeInfo = $"→ {refTable}";
            if (!string.IsNullOrEmpty(colMapping))
                typeInfo += $" ({colMapping})";
            if (deleteAction != "NO_ACTION")
                typeInfo += $" ON DELETE {deleteAction}";
            if (updateAction != "NO_ACTION")
                typeInfo += $" ON UPDATE {updateAction}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadConstraintsAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var constraints = connStr != null
            ? await _db.GetConstraintsAsync(connStr, db, schema, tableName)
            : await _db.GetConstraintsAsync(db, schema, tableName);

        foreach (var (name, constraintType, expression, columnName) in constraints)
        {
            var typeInfo = constraintType == "DEFAULT"
                ? $"DEFAULT on [{columnName}]: {expression}"
                : $"CHECK: {expression}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = typeInfo,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadUserTypesAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        var types = connStr != null
            ? await _db.GetUserTypesAsync(connStr, db)
            : await _db.GetUserTypesAsync(db);

        foreach (var (name, baseType, isTableType) in types)
        {
            var node = new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                NodeType = ObjectExplorerNodeType.Folder, // leaf info node
                TypeInfo = baseType,
                ConnectionId = folderNode.ConnectionId
            };

            folderNode.Children.Add(node);
        }
    }

    private async Task LoadDatabaseTriggersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var connStr = ResolveConnectionString(folderNode);

        var triggers = connStr != null
            ? await _db.GetDatabaseTriggersAsync(connStr, db)
            : await _db.GetDatabaseTriggersAsync(db);

        foreach (var (name, isEnabled, eventTypes) in triggers)
        {
            var typeInfo = isEnabled ? "" : "Disabled";
            if (!string.IsNullOrEmpty(eventTypes))
                typeInfo = string.IsNullOrEmpty(typeInfo) ? eventTypes : $"{typeInfo}, {eventTypes}";

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                NodeType = ObjectExplorerNodeType.Trigger,
                TypeInfo = typeInfo,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadColumnsAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var columns = connStr != null
            ? await _db.GetColumnsAsync(connStr, db, schema, tableName)
            : await _db.GetColumnsAsync(db, schema, tableName);

        foreach (var (name, typeName, maxLength, isNullable, isPk) in columns)
        {
            var typeInfo = FormatColumnType(typeName, maxLength);

            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = name,
                DatabaseName = db,
                Schema = schema,
                NodeType = ObjectExplorerNodeType.Column,
                TypeInfo = typeInfo,
                IsPrimaryKey = isPk,
                IsNullable = isNullable,
                ParentTableName = tableName,
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private async Task LoadTableTriggersAsync(ObjectExplorerNode folderNode)
    {
        var db = folderNode.DatabaseName;
        var schema = folderNode.Schema;
        var tableName = folderNode.ParentTableName;
        var connStr = ResolveConnectionString(folderNode);

        var triggers = connStr != null
            ? await _db.GetTriggersAsync(connStr, db)
            : await _db.GetTriggersAsync(db);

        var tableTrigs = triggers.Where(t =>
            t.ParentTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase));

        foreach (var t in tableTrigs)
        {
            folderNode.Children.Add(new ObjectExplorerNode
            {
                Name = t.Name, Schema = t.Schema, DatabaseName = db,
                ParentTableName = t.ParentTable,
                NodeType = ObjectExplorerNodeType.Trigger,
                TypeInfo = t.IsEnabled ? "" : "Disabled",
                ConnectionId = folderNode.ConnectionId
            });
        }
    }

    private static string FormatColumnType(string typeName, int maxLength)
        => SqlTypeFormatter.Format(typeName, maxLength);
}
