# Query Results Feedback Fixes

**Priority**: CRITICAL — this is the core execution loop of the IDE. Every fix here affects every single F5 press.

**Guiding principle**: Follow SSMS behavior. It's been battle-tested for 20+ years by millions of users. Where SSMS has a proven pattern, use it. Don't invent.

**Rule**: Build and test after each fix. These touch `ExecuteQueryCoreAsync` and `RunQueryAsync` which are the most exercised code paths in the app.

---

## How SSMS Handles It (The Reference)

Before touching any code, understand how SSMS works — this is what we're aligning to:

**Status bar**: Binary. Green checkmark + "Query executed successfully" OR yellow exclamation + "Query completed with errors." No middle ground. If ANY batch errors, the whole status is error. Execution time always shown.

**Messages tab**: Shows all output in order, per batch:
- `(N row(s) affected)` for DML — normal text color
- `Msg 208, Level 16, State 1, Line 2` + error text — **red**
- PRINT output — normal text color
- `Commands completed successfully.` for DDL with no row count

**Results tab**: ONLY for SELECT result sets. DML never gets a grid tab. Multiple SELECTs get separate grids.

**Error metadata**: SSMS uses `SqlException.Errors` collection, not just `ex.Message`. Each `SqlError` has: Number, Class (severity level), State, LineNumber, Message, Procedure. Formatted as: `Msg {Number}, Level {Class}, State {State}, Line {LineNumber}\n{Message}`

**Error navigation**: Double-clicking an error in Messages jumps to the line in the editor.

---

## The Current Bugs

### Bug 1: Success flash shown when batches error
`ExecuteQueryCoreAsync` catches `SqlException` per-batch inside the loop and adds to messages — it doesn't rethrow. Back in `RunQueryAsync`, the outer try/catch never fires. The flash always shows green ✓.

### Bug 2: DML row count not accessible to ViewModel
`RecordsAffected` is only added as a string `"(N rows affected)"` to the messages list. The ViewModel computes `totalRows` by summing `QueryResult.RowCount` — which is zero for DML since no QueryResult is created. Status bar shows "0 total rows" after an UPDATE that affected 100 rows.

### Bug 3: Error messages lack SQL Server metadata
Current code uses `ex.Message` and `ex.LineNumber` only. SSMS shows `Msg {Number}, Level {Class}, State {State}, Line {LineNumber}` which comes from `SqlException.Errors`. We're throwing away the Msg number and severity level.

### Bug 4: Messages panel has no visual distinction
Errors, row counts, PRINT output, and timing all render as plain text in the same color. SSMS renders errors in red.

---

## Fix 1: Track Execution Outcome Properly

**Where**: `DatabaseService.ExecuteQueryCoreAsync()` return type and `QueryTabViewModel.RunQueryAsync()`

**What**: The method currently returns `(List<QueryResult> Results, string Messages)`. We need to also return whether any errors occurred, and the total rows affected by DML.

Add a simple result summary:
```csharp
public class QueryExecutionResult
{
    public List<QueryResult> Results { get; set; } = [];
    public List<QueryMessage> Messages { get; set; } = [];
    public int TotalRowsAffected { get; set; }  // sum of all RecordsAffected across batches
    public bool HasErrors { get; set; }
    public int ErrorCount { get; set; }
}
```

In `ExecuteQueryCoreAsync`, accumulate `RecordsAffected` numerically:
```csharp
var totalRowsAffected = 0;
var hasErrors = false;
var errorCount = 0;

// ... inside batch loop, after reader completes:
if (reader.RecordsAffected >= 0)
{
    totalRowsAffected += reader.RecordsAffected;
    messages.Add(new QueryMessage { Type = MessageType.RowCount, 
        Text = $"({reader.RecordsAffected} row(s) affected)" });
}

// ... in catch (SqlException):
hasErrors = true;
errorCount++;
```

Return the summary alongside results and messages.

---

## Fix 2: Flash and Status Follow SSMS Binary Pattern

**Where**: `QueryTabViewModel.RunQueryAsync()` — the flash and status section

**Current**: Always fires `QueryStatusSeverity.Success`.

**Fix**: Binary, just like SSMS:
```csharp
var totalSelectRows = results.Where(r => r.Error == null).Sum(r => r.RowCount);
var hasErrors = executionResult.HasErrors;
var totalDmlRows = executionResult.TotalRowsAffected;

// Status bar: SSMS-style binary
if (hasErrors)
{
    QueryFlash?.Invoke("Query completed with errors", QueryStatusSeverity.Error);
    QueryStatusText = $"Errors, {elapsed}";
}
else
{
    // Build appropriate success message
    if (totalSelectRows > 0)
        QueryFlash?.Invoke($"✓ {totalSelectRows:N0} rows", QueryStatusSeverity.Success);
    else if (totalDmlRows > 0)
        QueryFlash?.Invoke($"✓ {totalDmlRows:N0} row(s) affected", QueryStatusSeverity.Success);
    else
        QueryFlash?.Invoke("Commands completed successfully", QueryStatusSeverity.Success);
    
    QueryStatusText = totalSelectRows > 0 
        ? $"{totalSelectRows:N0} rows, {elapsed}"
        : totalDmlRows > 0 
            ? $"{totalDmlRows:N0} affected, {elapsed}" 
            : elapsed;
}
```

No "warning" state. If errors exist, it's error. Period. This is how SSMS does it.

---

## Fix 3: Use SqlException.Errors for Proper Error Formatting

**Where**: `DatabaseService.ExecuteQueryCoreAsync()` — the SqlException catch block

**Current**:
```csharp
catch (SqlException ex)
{
    var errorMsg = ex.LineNumber > 0
        ? $"Error (Line {ex.LineNumber}): {ex.Message}"
        : $"Error: {ex.Message}";
    messages.Add(errorMsg);
    results.Add(new QueryResult { Error = errorMsg });
}
```

**Fix**: Use `SqlException.Errors` collection like SSMS does:
```csharp
catch (SqlException ex)
{
    foreach (SqlError err in ex.Errors)
    {
        // SSMS format: "Msg 208, Level 16, State 1, Line 2\nInvalid object name 'xxx'."
        var header = $"Msg {err.Number}, Level {err.Class}, State {err.State}, Line {err.LineNumber}";
        messages.Add(new QueryMessage 
        { 
            Type = MessageType.Error, 
            Text = $"{header}\n{err.Message}",
            LineNumber = err.LineNumber  // for double-click navigation
        });
    }
    
    // Still add a QueryResult with error for the result tab indicator
    var primaryError = ex.Errors[0];
    results.Add(new QueryResult 
    { 
        Error = $"Msg {primaryError.Number}, Level {primaryError.Class}, State {primaryError.State}, Line {primaryError.LineNumber}: {primaryError.Message}" 
    });
    
    hasErrors = true;
    errorCount++;
}
```

This gives us SSMS-identical error output. Multiple errors from one batch (e.g. cascading errors) each get their own message line, each navigable.

---

## Fix 4: Structured Messages with Color

**Where**: New `QueryMessage` model + Messages panel rendering

### 4A. Create the message model

```csharp
// Models/QueryMessage.cs
public enum MessageType { Info, RowCount, Error, Print, Timing }

public class QueryMessage
{
    public MessageType Type { get; set; }
    public string Text { get; set; } = "";
    public int LineNumber { get; set; } = -1;  // for error click-to-navigate
}
```

### 4B. Change ExecuteQueryCoreAsync to use QueryMessage

Replace `List<string> messages` with `List<QueryMessage> messages` throughout:

```csharp
// InfoMessage handler (PRINT output):
conn.InfoMessage += (_, e) => messages.Add(new QueryMessage { Type = MessageType.Print, Text = e.Message });

// Row count:
messages.Add(new QueryMessage { Type = MessageType.RowCount, Text = $"({reader.RecordsAffected} row(s) affected)" });

// Timing:
messages.Add(new QueryMessage { Type = MessageType.Timing, Text = $"Total execution time: {sw.ElapsedMilliseconds}ms" });

// Errors: handled by Fix 3 above

// Cancellation:
messages.Add(new QueryMessage { Type = MessageType.Info, Text = "Query was cancelled by user." });
```

### 4C. Update ViewModel to expose structured messages

Change `Messages` property from `string` to `ObservableCollection<QueryMessage>`:
```csharp
[ObservableProperty] private ObservableCollection<QueryMessage> _messages = [];
```

### 4D. Update Messages panel rendering

Replace the TextBlock with an ItemsControl that colors by type:
- `MessageType.Error` → Red foreground (use `ButtonDanger` theme resource)
- `MessageType.RowCount` → Normal foreground
- `MessageType.Print` → Normal foreground  
- `MessageType.Timing` → Muted/secondary foreground
- `MessageType.Info` → Normal foreground

Error messages should be clickable (double-click or single-click) to navigate to the line in the editor — this is standard SSMS behavior. Use the `LineNumber` property on the QueryMessage.

**XAML sketch:**
```xml
<ItemsControl ItemsSource="{Binding Messages}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding Text}" 
                       FontFamily="Consolas, Menlo, Monaco, monospace"
                       FontSize="12" TextWrapping="Wrap"
                       Foreground="{Binding Type, Converter={StaticResource MessageTypeToColorConverter}}"
                       Cursor="{Binding LineNumber, Converter={StaticResource LineNumberToCursorConverter}}"/>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

Or simpler: build the ItemsControl in code-behind using the QueryMessage list, applying foreground colors directly. This avoids needing converters.

---

## Fix 5: Error Click-to-Navigate

**Where**: Messages panel event handling (QueryTabView)

**What**: When user clicks/double-clicks an error message in the Messages panel, navigate to the error line in the editor. This is core SSMS behavior.

```csharp
// On double-click of a message item:
if (message.Type == MessageType.Error && message.LineNumber > 0)
{
    SqlEditor.TextArea.Caret.Line = message.LineNumber;
    SqlEditor.TextArea.Caret.Column = 1;
    SqlEditor.ScrollTo(message.LineNumber, 1);
    SqlEditor.Focus();
}
```

Note: `SqlError.LineNumber` is relative to the batch, not the full script. If using GO-separated batches, the line number needs to be offset by the batch's starting line in the full script. Track the starting line of each batch during `SplitOnGo` and add it to the error's LineNumber.

---

## Execution Order

1. **Fix 1** — Add `QueryExecutionResult` summary, track `TotalRowsAffected` and `HasErrors` → build & test
2. **Fix 2** — Fix flash/status to use SSMS binary pattern → build & test
3. **Fix 3** — Use `SqlException.Errors` for proper error formatting → build & test
4. **Fix 4** — Structured `QueryMessage` with colored rendering → build & test
5. **Fix 5** — Error click-to-navigate → build & test

Fixes 1-3 are surgical and low-risk. Fix 4 changes the Messages property type which touches more surface area. Fix 5 builds on Fix 4.

**Important**: Apply the same fixes to `ExecuteWithTraceAsync` which has a parallel batch loop with identical issues.

---

## Test Scenarios

After all fixes, verify these match SSMS behavior:

| Scenario | SSMS Status Bar | Expected Flash | Expected Messages |
|----------|----------------|----------------|-------------------|
| `SELECT TOP 10 * FROM t` | Query executed successfully | ✓ 10 rows | (10 row(s) affected) |
| `UPDATE t SET x=1 WHERE id=5` (3 rows) | Query executed successfully | ✓ 3 row(s) affected | (3 row(s) affected) |
| `DELETE FROM t WHERE 1=0` (0 rows) | Query executed successfully | ✓ 0 row(s) affected | (0 row(s) affected) |
| `SELECT * FROM nonexistent` | Query completed with errors | Query completed with errors | **Red**: Msg 208, Level 16, State 1, Line 1 / Invalid object name 'nonexistent'. |
| `UPDATE t SET x=1` then `SELECT * FROM nonexistent` | Query completed with errors | Query completed with errors | (3 row(s) affected) / **Red**: Msg 208... |
| `SELECT * FROM t` then `SELECT 1/0` | Query completed with errors | Query completed with errors | (10 row(s) affected) / **Red**: Msg 8134... |
| `PRINT 'hello'` | Query executed successfully | Commands completed successfully | hello |
| `USE master` | Query executed successfully | Commands completed successfully | Commands completed successfully. |
| Query cancelled by user | Query was cancelled by user | Cancelled | Query was cancelled by user. |

**Key validation**: In the "UPDATE then error" scenario, the Messages panel must show BOTH the "(3 row(s) affected)" AND the red error — not just the error. This is the exact bug Ömer reported.
