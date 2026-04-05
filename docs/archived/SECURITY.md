# Security & Code Quality Audit

**Created:** March 30, 2026
**Scope:** Full codebase review — security, error handling, memory, thread safety, missing safeguards.
**Audited by:** Claude (architect role), reviewed by CC.

---

## Audit Coverage

The following files were read and examined in full:

**Services (all):** `DatabaseService.cs`, `DataEditService.cs`, `DataCompareService.cs`, `PasswordStore.cs`, `SettingsService.cs`, `SessionService.cs`, `QueryFileService.cs`, `IntellisenseService.cs`, `GitExportService.cs`, `UpdateService.cs`, `SleepDetector.cs`, `ThemeManager.cs`, `AppVersion.cs`, `SqlFormatterService.cs`, `SqlQuoterService.cs`, `SqlTypeFormatter.cs`, `TableCompareService.cs`, `ExportService.cs`, `JobScheduleFormatter.cs`, `SqlCompletionData.cs`, `SqlSyntaxHighlighter.cs`, `PlanTranslator.cs`, `PlanXmlHelper.cs`, `PlanScorer.cs`

**ViewModels (all):** `MainWindowViewModel.cs`, `CompareViewModel.cs`, `QueryTabViewModel.cs`, `ActivityViewModel.cs`, `QueryEditorHostViewModel.cs`, `ObjectExplorerViewModel.cs`, `PlanViewModel.cs`, `ConnectionViewModel.cs`

**Views (security-relevant):** `MainWindow.axaml.cs`, `ActivityView.axaml.cs`, `AlterSequenceDialog.axaml.cs`, `CompareView.axaml.cs`

**Models:** `ConnectionSettings.cs`, `EditableRow.cs`, `QueryResult.cs`, `SavedConnection` (in SettingsService.cs)

**Config:** `Info.plist`, `SqlVersionControl.csproj`, `app.manifest`, `.github/workflows/release.yml`

### Explicitly Cleared (No Issues Found)

- **DataEditService.cs** — All DML generation (INSERT/UPDATE/DELETE) uses parameterized queries (`@set_`, `@pk_`, `@ins_`). Column/table names come from the app's own metadata queries and are bracket-escaped. Single-row verification (`affected != 1` → rollback) is a solid safety pattern. `GeneratePreviewSql` is display-only and uses `FormatValue` with proper string escaping.
- **DataCompareService.cs** — PK detection and row fetching use parameterized queries. Schema/table names are bracket-escaped for identifier positions.
- **All stored procedure calls** — Job management (`sp_start_job`, `sp_stop_job`, `sp_update_job`, `sp_add_jobstep`, `sp_update_jobstep`, `sp_delete_jobstep`, `sp_add_jobschedule`, `sp_update_schedule`, `sp_detach_schedule`) all use `CommandType.StoredProcedure` with parameterized values.
- **KillSessionAsync** — Uses `$"KILL {sessionId}"` where `sessionId` is `int` — type-safe, no injection possible. `@@SPID` check prevents self-kill.
- **QueryFileService.cs** — File paths come from the app's save dialog or internal queries folder enumeration, not from `.sql` metadata headers. No path traversal risk.

### Accepted Risks (Documented, No Mitigation Needed)

- **Passwords in managed memory:** `PasswordStore` uses `Dictionary<string, string>`. Passwords reside in managed heap memory, potentially surviving GC generations and written to swap/pagefile. `SecureString` is deprecated in modern .NET. Every database tool has this same characteristic. No practical alternative exists.
- **Settings file permissions:** `settings.json` stores server names, database names, and usernames (no passwords). On macOS, `~/Library/Application Support/` is user-owned. On Windows, `%APPDATA%` is per-user. .NET's `File.WriteAllText` inherits directory permissions. Adequate for a single-user desktop app.
- **PasswordStore AES key derivation:** On macOS/Linux, the AES-256 key is derived via PBKDF2 (100,000 iterations, SHA-256) from `MachineName|UserName|SqlVersionControl`. This is per-machine, per-user — not hardcoded. On Windows, DPAPI with `DataProtectionScope.CurrentUser` is used. Both are the correct approach for desktop credential storage without OS keychain integration.

---

## Section 1 — Security Fixes (Must Do)

### 1.1 Connection String Injection via String Interpolation
**Status:** DONE (2026-03-29)
**Files:** `Models/ConnectionSettings.cs`, `ViewModels/CompareViewModel.cs`
**Risk:** Medium — self-inflicted, but a real bug

`ConnectionSettings.ConnectionString` builds the connection string with `$"Server={Server};Database={Database};User Id={Username};Password={Password};..."`. If any field contains semicolons (particularly passwords like `foo;Encrypt=false;`), extra parameters get injected.

`CompareViewModel.BuildConnectionString()` has the same problem — three copies of raw string interpolation.

**Fix:** Replace all connection string building with `SqlConnectionStringBuilder`:
```csharp
public string ConnectionString
{
    get
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 10
        };

        if (UseWindowsAuth)
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = Username;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }
}
```

Do the same in `CompareViewModel.BuildConnectionString()`. Better yet: make `ConnectionSettings` the single source of truth — have `CompareViewModel` construct a `ConnectionSettings` then call `.ConnectionString`.

### 1.2 TrustServerCertificate=True Hardcoded
**Status:** DONE (2026-03-29)
**Files:** `Models/ConnectionSettings.cs`, `ViewModels/CompareViewModel.cs`
**Risk:** Medium — disables TLS certificate validation on every connection

Every connection the app makes has `TrustServerCertificate=True`, disabling MITM protection. On corporate networks with TLS-intercepting proxies or compromised DNS, this is a real attack surface.

**Fix:** Add a per-connection "Trust Server Certificate" checkbox, defaulting to `true` for backward compatibility. Store in `SavedConnection`. When the Connection Manager feature is built (see `docs/CONNECTION-MANAGER.md`), surface this as a toggle in the edit form. For now, making it configurable in `ConnectionSettings` is sufficient.

Note: if this app ever goes public/open-source, the default should flip to `false`. The `true` default is a pragmatic concession for existing internal users only.

### 1.3 Single-Quote Injection in Index Analysis Queries
**Status:** DONE (2026-03-29)
**Files:** `Services/DatabaseService.cs` — `GetUnusedIndexesAsync`, `GetMissingIndexesAsync`
**Risk:** Low — database names with single quotes are extremely rare

Both methods use `DB_ID('{safeDb}')` where `safeDb` is bracket-escaped (`Replace("]", "]]")`), but bracket-escaping doesn't protect string context. A database name containing `'` would break the SQL.

**Fix:** Add single-quote escaping for string-context usage:
```csharp
var safeDbForString = database.Replace("'", "''");
// Use: DB_ID('{safeDbForString}') for string context
// Keep: [{safeDb}] for identifier context
```

### 1.4 DDL Audit Table Source — Unsanitized Interpolation
**Status:** DONE (2026-03-29)
**Files:** `Services/DatabaseService.cs` — `SyncFromDdlLogAsync`
**Risk:** Low — configured by app owner only

The `ddlTable` variable from settings is interpolated directly into SQL: `FROM {ddlTable}`.

**Fix:** Parse into database/schema/table parts and bracket-escape each:
```csharp
private static string SafeDdlTableRef(string ddlSource)
{
    var parts = ddlSource.Split('.', 3);
    if (parts.Length == 3)
        return $"[{parts[0].Replace("]", "]]")}].[{parts[1].Replace("]", "]]")}].[{parts[2].Replace("]", "]]")}]";
    if (parts.Length == 2)
        return $"[{parts[0].Replace("]", "]]")}].dbo.[{parts[1].Replace("]", "]]")}]";
    throw new ArgumentException("DDL audit source must be in Database.Schema.Table format");
}
```

Add input validation in the Settings dialog — reject values that don't match `word.word.word` or `word.word` pattern.

---

## Section 2 — Missing Confirmation Dialogs

### What Already Has Confirmations (Verified in View Code-Behind)

All verified by reading `ActivityView.axaml.cs`, `MainWindow.axaml.cs`:

| Action | Confirmation | Details |
|--------|-------------|---------|
| Kill Session | ✅ Yes | Detailed dialog: session ID, login, database, elapsed time, statement preview |
| Start Job | ✅ Yes | Simple confirm: "Start job '{name}'?" |
| Stop Job | ✅ Yes | Simple confirm: "Stop job '{name}'?" |
| Enable/Disable Job | ✅ Yes | Simple confirm: "{action} job '{name}'?" |
| Delete Job Step | ✅ Yes | Simple confirm: "Delete step '{name}'?" |
| Rollback | ✅ Yes | RollbackDialog with version details |
| Deploy (code objects) | ✅ Yes | DeployDialog with prod detection (.15 → "PRODUCTION" label) |
| Deploy (table structure) | ✅ Yes | DeployDialog with "TABLE STRUCTURE CHANGE" warning |
| Deploy Data Rows | ✅ Yes | DeployDialog with row count and prod detection |
| Deploy Single Field | ✅ Yes | DeployDialog with old→new value display |

### What's Missing

### 2.1 Schedule Removal — No Confirmation
**Status:** DONE (2026-03-29)
**Files:** `Views/ActivityView.axaml` (XAML binding), `ViewModels/ActivityViewModel.cs`

"Remove Schedule" button is bound directly to `RemoveScheduleCommand` via XAML `Command="{Binding RemoveScheduleCommand}"` — no confirmation wrapper in code-behind. Removing a schedule from a production job silently stops it from running.

**Fix:** Same pattern as other Activity actions — add `ConfirmAndRemoveScheduleAsync()` in `ActivityView.axaml.cs`, wire the button click in code-behind instead of XAML Command binding:
```
"Remove schedule from job '{jobName}'? The job will no longer run automatically."
```

### 2.2 Sequence ALTER — No Explicit Warning
**Status:** DONE (2026-03-29)
**Files:** `Views/AlterSequenceDialog.axaml.cs`

`AlterSequenceDialog` is a value input dialog with OK/Cancel. No explicit warning about consequences. Altering a sequence restart value can cause duplicate keys or skipped ranges.

**Fix:** Add a warning TextBlock in the dialog: "Changing the sequence value may cause duplicate key errors or skipped ranges." Make it visible but not blocking — the dialog itself (type value + click OK) provides sufficient friction. No separate confirmation dialog needed.

---

## Section 3 — Code Quality Fixes

### 3.1 ConvertToCreateOrAlter Is Duplicated
**Status:** DONE (2026-03-29)
**Files:** `Services/DatabaseService.cs`, `ViewModels/CompareViewModel.cs`
**Violates:** Project Rule #1 — Single Source of Truth

Identical method in two places. If one gets a bug fix, the other won't.

**Fix:** Make `DatabaseService.ConvertToCreateOrAlter()` `internal static` (or `public static`). Delete the copy in `CompareViewModel`, call `DatabaseService.ConvertToCreateOrAlter()`.

### 3.2 ActivityViewModel.Dispose() Not IDisposable
**Status:** DONE (2026-03-29)
**Files:** `ViewModels/ActivityViewModel.cs`

Has a `Dispose()` method that stops the auto-refresh timer, but doesn't implement `IDisposable`. Nothing calls it through standard disposal patterns.

**Fix:** Add `IDisposable` to the class declaration. Ensure `ActivityView` calls `Dispose()` when the view is torn down or the app exits.

### 3.3 RollbackToVersionAsync Swallows Exception Details
**Status:** DONE (2026-03-29)
**Files:** `Services/DatabaseService.cs`

Returns `bool` but discards the exception message. ViewModel shows generic "Rollback failed - check permissions."

**Fix:** Change return to `(bool Success, string? Error)`. Update `MainWindowViewModel.RollbackAsync` to display the actual error.

### 3.4 No Application-Level Logging
**Status:** DONE (2026-03-29)
**Files:** Multiple — `PasswordStore.cs`, `SettingsService.cs`, `SessionService.cs`, all catch blocks

`Console.WriteLine` goes nowhere in a packaged desktop app. Empty catch blocks hide failures.

**Fix:** Simple file logger writing to the app data folder (`logs/app.log`). Static helper, timestamped lines, cap at ~5MB. Replace all `Console.WriteLine` and empty catches.
 Log rotation: on startup, if `app.log` exceeds 5MB, rename to `app.log.old` (overwriting any previous `.old`) and start fresh. One backup file, no complexity.

### 3.5 CompareViewModel Creates Orphan DatabaseService Instances
**Status:** DONE (2026-03-29)
**Files:** `ViewModels/CompareViewModel.cs` — `LoadTableObjectsAsync`

`new DatabaseService()` creates a disconnected instance just to call `GetTableStructureAsync`. Wasteful and architecturally messy.

**Fix:** Pass the existing `DatabaseService` into `CompareViewModel`, or make `GetTableStructureAsync` static.

### 3.6 CancellationTokenSource Not Disposed
**Status:** DONE (2026-03-29)
**Files:** `ViewModels/MainWindowViewModel.cs` — `_codeSearchCts`

Canceled but never explicitly disposed. Minor resource leak.

**Fix:** `_codeSearchCts?.Cancel(); _codeSearchCts?.Dispose();` before reassigning.

---

## Section 4 — Nice to Have

### 4.1 SPID Check Can Go Stale
**Status:** DONE (2026-03-29)
**Files:** `ViewModels/ActivityViewModel.cs`

`_currentSpid` fetched once at init. Connection pool recycling changes the SPID but the check doesn't update.

**Fix:** Re-fetch `@@SPID` on each refresh cycle (0ms query).

### 4.2 Production Server Detection Is Hardcoded
**Status:** TODO
**Files:** `ViewModels/CompareViewModel.cs`

`Server.EndsWith(".15")` is the only prod detection. Fragile.

**Fix:** Solved by the `Environment` field in Connection Manager — see `docs/CONNECTION-MANAGER.md`. Migration default should be `null`/"Unknown" (not "Dev" or "Production") to avoid silently losing safety checks or spamming dialogs.

### 4.3 PasswordStore Thread Safety
**Status:** DONE (2026-03-29)
**Files:** `Services/PasswordStore.cs`

Static `Dictionary<string, string>` is not thread-safe. Currently safe because all callers are on the UI thread.

**Fix:** Replace with `ConcurrentDictionary<string, string>`.

### 4.4 DataEditService.FormatValue Default Case
**Status:** DONE (2026-03-29)
**Files:** `Services/DataEditService.cs`

The `_ => value.ToString() ?? "NULL"` fallback could produce invalid SQL preview for exotic types. Preview-only, not execution.

**Fix:** Wrap unknown types: `$"/* unsupported: {value.GetType().Name} */"`.

### 4.5 MainWindowViewModel Auto-Sync Timer Never Stopped
**Status:** DONE (2026-03-29)
**Files:** `ViewModels/MainWindowViewModel.cs`

`_autoSyncTimer` started but no shutdown method. Fine for single-instance app, but leaks if ViewModel recreated.

**Fix:** Add `StopAutoSyncTimer()`, call from `MainWindow.OnClosing`.

---

## Section 5 — Thread Safety Notes (Reference)

Not bugs in current single-threaded-UI usage, but would become bugs if architecture changes.

### 5.1 DatabaseService._connectionString
Plain `string` field. `SetConnection()` writes it, multiple async methods read it. `SetHistoryConnection()` and auto-sync both go through the same instance. If they ever overlap, torn reads.

**If this matters:** Give each view its own `DatabaseService` instance, or pass connection strings explicitly (per-tab methods already do this).

### 5.2 ObservableCollection Mutations From Background Threads
`CommunityToolkit.Mvvm` `[ObservableProperty]` raises `PropertyChanged` on the calling thread. If any async method modifies an `ObservableCollection` without dispatching to UI thread, you get silent corruption or crash.

**Current state:** The codebase correctly uses `Dispatcher.UIThread.Post()` for collection mutations in `ActivityViewModel` (sessions, jobs, categories) and `MainWindowViewModel` (search results). `CompareViewModel.ScanForDifferencesAsync` uses `Dispatcher.UIThread.Post` for progress updates. No violations found, but any new async code adding to ObservableCollections must follow this pattern.

### 5.3 ActivityViewModel.UpdateConnectionString Fire-and-Forget
Spawns `Task.Run` to fetch SPID. If result arrives after context change, `_currentSpid` gets overwritten with stale value.

---

## Recommended Execution Order

1. **1.1** — Connection string builder (highest real-world impact, touches same files as other fixes)
2. **1.2** — TrustServerCertificate configurable (do alongside 1.1 since both touch ConnectionSettings)
3. **1.3, 1.4** — SQL injection fixes (small, fast)
4. **3.3** — Swallowed exception details (quick win, huge debugging value)
5. **3.4** — File logging (unblocks all future debugging)
6. **2.1, 2.2** — Missing confirmation dialogs (schedule removal, sequence warning)
7. **3.1, 3.2, 3.5, 3.6** — Code quality (mechanical fixes)
8. **4.1–4.5** — Nice-to-haves

Connection Manager is a separate initiative — see `docs/CONNECTION-MANAGER.md`.

---

## Completed Items

- **1.1** (2026-03-29) — ConnectionSettings + CompareViewModel now use SqlConnectionStringBuilder. CompareViewModel delegates to ConnectionSettings as single source of truth.
- **1.2** (2026-03-29) — Added TrustServerCertificate property to ConnectionSettings (default true), wired through SqlConnectionStringBuilder.
- **1.3** (2026-03-29) — Added `safeDbStr` (single-quote escaped) for DB_ID() string context in both index analysis queries.
- **1.4** (2026-03-29) — Added SafeDdlTableRef() to bracket-escape DDL table parts. Added regex validation in SettingsDialog on save.
- **2.1** (2026-03-29) — Removed XAML Command binding, wired RemoveScheduleButton.Click through ConfirmAndRemoveScheduleAsync() in code-behind.
- **2.2** (2026-03-29) — Added warning TextBlock to AlterSequenceDialog about duplicate keys / skipped ranges.
- **3.1** (2026-03-29) — Made DatabaseService.ConvertToCreateOrAlter internal static, deleted duplicate from CompareViewModel.
- **3.2** (2026-03-29) — ActivityViewModel now implements IDisposable, MainWindow.OnClosing calls Dispose().
- **3.3** (2026-03-29) — RollbackToVersionAsync returns (bool Success, string? Error), MainWindowViewModel shows actual error.
- **3.4** (2026-03-29) — Created AppLogger.cs (static file logger, 5MB cap, .old rotation). Replaced all Console.WriteLine and empty catches across SettingsService, PasswordStore, SessionService, QueryTabView, QueryEditorHost, ObjectExplorerViewModel.
- **3.5** (2026-03-29) — Made GetTableStructureAsync static. CompareViewModel calls DatabaseService.GetTableStructureAsync() directly.
- **3.6** (2026-03-29) — Added _codeSearchCts?.Dispose() before reassignment.
- **4.1** (2026-03-29) — SPID re-fetched on every RefreshAsync() cycle.
- **4.3** (2026-03-29) — PasswordStore._passwords changed to ConcurrentDictionary.
- **4.4** (2026-03-29) — FormatValue now has explicit numeric type matches; unknown types return /* unsupported */ comment.
- **4.5** (2026-03-29) — Added StopAutoSyncTimer(), called from MainWindow.OnClosing.

---

## Rules for This Doc
1. New items append to the relevant section — never modify completed items.
2. Update status markers: TODO → IN PROGRESS → DONE (date).
3. When an item is done, move it to Completed section with a one-line summary.
