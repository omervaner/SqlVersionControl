using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class ActivityViewModel : ViewModelBase, IDisposable
{
    private readonly DatabaseService _db;
    private string? _connectionString;
    private int _currentSpid;
    private DispatcherTimer? _autoRefreshTimer;

    // ── Observable Properties ─────────────────────────────────────

    // Sub-tab selection
    [ObservableProperty]
    private bool _isSessionsTabActive = true;

    [ObservableProperty]
    private bool _isJobsTabActive;

    // Sessions
    [ObservableProperty]
    private ObservableCollection<SessionRow> _sessions = new();

    [ObservableProperty]
    private SessionRow? _selectedSession;

    [ObservableProperty]
    private string _blockingChainText = "";

    [ObservableProperty]
    private bool _hasBlockingChains;

    // Session filters
    [ObservableProperty]
    private bool _hideSleeping = true;

    [ObservableProperty]
    private string _sessionSearchText = "";

    [ObservableProperty]
    private string? _sessionDatabaseFilter;

    [ObservableProperty]
    private ObservableCollection<string> _sessionDatabases = new() { "(All)" };

    // Jobs
    [ObservableProperty]
    private ObservableCollection<JobRow> _jobs = new();

    [ObservableProperty]
    private JobRow? _selectedJob;

    [ObservableProperty]
    private ObservableCollection<JobStepRow> _jobSteps = new();

    [ObservableProperty]
    private ObservableCollection<JobHistoryRow> _jobHistory = new();

    [ObservableProperty]
    private bool _isJobDetailVisible;

    [ObservableProperty]
    private bool _isJobGeneralTabActive = true;

    [ObservableProperty]
    private bool _isJobStepsTabActive;

    [ObservableProperty]
    private bool _isJobScheduleTabActive;

    [ObservableProperty]
    private bool _isJobHistoryTabActive;

    // General tab editing
    [ObservableProperty]
    private string _editJobName = "";

    [ObservableProperty]
    private string _editJobDescription = "";

    [ObservableProperty]
    private bool _editJobEnabled;

    [ObservableProperty]
    private string _editJobCategory = "";

    [ObservableProperty]
    private ObservableCollection<string> _allCategories = new();

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    // Steps tab editing
    [ObservableProperty]
    private JobStepRow? _selectedStep;

    [ObservableProperty]
    private string _editStepCommand = "";

    // Schedule tab editing
    [ObservableProperty]
    private bool _hasSchedule;

    [ObservableProperty]
    private int _editScheduleId;

    [ObservableProperty]
    private int _editFreqType = 4;

    [ObservableProperty]
    private int _editFreqInterval = 1;

    [ObservableProperty]
    private int _editFreqSubdayType = 1;

    [ObservableProperty]
    private int _editFreqSubdayInterval = 1;

    [ObservableProperty]
    private int _editActiveStartHour;

    [ObservableProperty]
    private int _editActiveStartMinute;

    // Weekly day checkboxes
    [ObservableProperty] private bool _schedSun;
    [ObservableProperty] private bool _schedMon;
    [ObservableProperty] private bool _schedTue;
    [ObservableProperty] private bool _schedWed;
    [ObservableProperty] private bool _schedThu;
    [ObservableProperty] private bool _schedFri;
    [ObservableProperty] private bool _schedSat;

    [ObservableProperty]
    private string _schedulePreview = "";

    // Job filters
    [ObservableProperty]
    private string _jobSearchText = "";

    [ObservableProperty]
    private string _jobEnabledFilter = "All";

    [ObservableProperty]
    private string _jobOutcomeFilter = "All";

    [ObservableProperty]
    private string _jobCategoryFilter = "(All)";

    [ObservableProperty]
    private bool _showOnlyRunning;

    [ObservableProperty]
    private ObservableCollection<string> _jobCategories = new() { "(All)" };

    // Auto-refresh
    [ObservableProperty]
    private bool _autoRefreshEnabled;

    [ObservableProperty]
    private int _autoRefreshIntervalSeconds = 5;

    // Status
    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    // ── Raw unfiltered data ───────────────────────────────────────

    private List<SessionRow> _allSessions = new();
    private List<JobRow> _allJobs = new();

    // ── Events ────────────────────────────────────────────────────

    /// <summary>Fired when user wants to open a query in a new editor tab.</summary>
    public event Action<string>? OpenInEditorRequested;

    /// <summary>Fired when a kill/action status message should show.</summary>
    public event Action<string>? ActionCompleted;

    // ── Constructor ───────────────────────────────────────────────

    public ActivityViewModel(DatabaseService db)
    {
        _db = db;
    }

    public async Task InitializeAsync(string connectionString)
    {
        _connectionString = connectionString;
        try
        {
            _currentSpid = await _db.GetCurrentSpidAsync(connectionString);
        }
        catch { _currentSpid = -1; }

        await RefreshAsync();
    }

    public void UpdateConnectionString(string connectionString)
    {
        _connectionString = connectionString;
        // Re-fetch SPID for the new connection
        _ = Task.Run(async () =>
        {
            try { _currentSpid = await _db.GetCurrentSpidAsync(connectionString); }
            catch { _currentSpid = -1; }
        });
    }

    // ── Refresh ───────────────────────────────────────────────────

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_connectionString == null) return;
        IsLoading = true;

        try
        {
            // Re-fetch SPID each cycle — connection pool may recycle our connection
            try { _currentSpid = await _db.GetCurrentSpidAsync(_connectionString); }
            catch { _currentSpid = -1; }

            if (IsSessionsTabActive)
                await RefreshSessionsAsync();
            else
                await RefreshJobsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshSessionsAsync()
    {
        var rawSessions = await _db.GetActiveSessionsAsync(_connectionString!);

        _allSessions = rawSessions.Select(r => new SessionRow
        {
            SessionId = Convert.ToInt32(r["Session ID"]),
            Login = r["Login"]?.ToString() ?? "",
            Database = r["Database"]?.ToString() ?? "",
            SessionStatus = r["Session Status"]?.ToString() ?? "",
            RequestStatus = r["Request Status"]?.ToString() ?? "",
            Command = r["Command"]?.ToString() ?? "",
            WaitType = r["Wait Type"]?.ToString() ?? "",
            WaitTimeMs = r["Wait Time (ms)"] != null ? Convert.ToInt32(r["Wait Time (ms)"]) : 0,
            BlockingSession = r["Blocking Session"] != null ? Convert.ToInt32(r["Blocking Session"]) : 0,
            CpuMs = r["CPU (ms)"] != null ? Convert.ToInt64(r["CPU (ms)"]) : 0,
            Reads = r["Reads"] != null ? Convert.ToInt64(r["Reads"]) : 0,
            Writes = r["Writes"] != null ? Convert.ToInt64(r["Writes"]) : 0,
            LogicalReads = r["Logical Reads"] != null ? Convert.ToInt64(r["Logical Reads"]) : 0,
            ElapsedSeconds = r["Elapsed (s)"] != null ? Convert.ToInt32(r["Elapsed (s)"]) : 0,
            PercentComplete = r["% Complete"] != null ? Convert.ToDouble(r["% Complete"]) : 0,
            OpenTransactions = r["Open Trans"] != null ? Convert.ToInt32(r["Open Trans"]) : 0,
            Host = r["Host"]?.ToString() ?? "",
            Program = r["Program"]?.ToString() ?? "",
            QueryText = r["Query Text"]?.ToString() ?? "",
            CurrentStatement = r["Current Statement"]?.ToString() ?? "",
            IsCurrentSession = Convert.ToInt32(r["Session ID"]) == _currentSpid
        }).ToList();

        // Update database filter options
        var dbs = _allSessions.Select(s => s.Database).Where(d => !string.IsNullOrEmpty(d)).Distinct().OrderBy(d => d).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            SessionDatabases.Clear();
            SessionDatabases.Add("(All)");
            foreach (var db in dbs) SessionDatabases.Add(db);
        });

        ApplySessionFilters();
        UpdateBlockingChains();

        StatusMessage = $"Sessions: {_allSessions.Count} total, {Sessions.Count} shown — {DateTime.Now:HH:mm:ss}";
    }

    private async Task RefreshJobsAsync()
    {
        var rawJobs = await _db.GetJobsDashboardAsync(_connectionString!);

        _allJobs = rawJobs.Select(r =>
        {
            var freqType = r["FreqType"] != null ? Convert.ToInt32(r["FreqType"]) : 0;
            var freqInterval = r["FreqInterval"] != null ? Convert.ToInt32(r["FreqInterval"]) : 0;
            var freqSubdayType = r["FreqSubdayType"] != null ? Convert.ToInt32(r["FreqSubdayType"]) : 0;
            var freqSubdayInterval = r["FreqSubdayInterval"] != null ? Convert.ToInt32(r["FreqSubdayInterval"]) : 0;
            var activeStartTime = r["ActiveStartTime"] != null ? Convert.ToInt32(r["ActiveStartTime"]) : 0;
            var durationSec = r["LastDurationSec"] != null ? Convert.ToInt32(r["LastDurationSec"]) : 0;

            return new JobRow
            {
                JobName = r["JobName"]?.ToString() ?? "",
                IsEnabled = r["IsEnabled"] != null && Convert.ToInt32(r["IsEnabled"]) == 1,
                Category = r["Category"]?.ToString() ?? "",
                CurrentStatus = r["CurrentStatus"]?.ToString() ?? "Idle",
                LastRunOutcome = r["LastRunOutcome"]?.ToString() ?? "Unknown",
                LastRunDate = r["LastRunDate"] as DateTime?,
                LastDurationSeconds = durationSec,
                LastDurationDisplay = durationSec > 0 ? JobScheduleFormatter.FormatDuration(durationSec) : "",
                NextRunDate = r["NextRunDate"] as DateTime?,
                Schedule = freqType > 0
                    ? JobScheduleFormatter.Format(freqType, freqInterval, freqSubdayType, freqSubdayInterval, activeStartTime)
                    : "No schedule",
                Description = r["JobDescription"]?.ToString() ?? "",
                StepCount = r["StepCount"] != null ? Convert.ToInt32(r["StepCount"]) : 0
            };
        }).ToList();

        // Update category filter
        var cats = _allJobs.Select(j => j.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            JobCategories.Clear();
            JobCategories.Add("(All)");
            foreach (var cat in cats) JobCategories.Add(cat);
        });

        ApplyJobFilters();
        StatusMessage = $"Jobs: {_allJobs.Count} total, {Jobs.Count} shown — {DateTime.Now:HH:mm:ss}";
    }

    // ── Filters ───────────────────────────────────────────────────

    partial void OnHideSleepingChanged(bool value) => ApplySessionFilters();
    partial void OnSessionSearchTextChanged(string value) => ApplySessionFilters();
    partial void OnSessionDatabaseFilterChanged(string? value) => ApplySessionFilters();
    partial void OnJobSearchTextChanged(string value) => ApplyJobFilters();
    partial void OnJobEnabledFilterChanged(string value) => ApplyJobFilters();
    partial void OnJobOutcomeFilterChanged(string value) => ApplyJobFilters();
    partial void OnJobCategoryFilterChanged(string value) => ApplyJobFilters();
    partial void OnShowOnlyRunningChanged(bool value) => ApplyJobFilters();

    private void ApplySessionFilters()
    {
        var filtered = _allSessions.AsEnumerable();

        if (HideSleeping)
            filtered = filtered.Where(s => !string.IsNullOrEmpty(s.RequestStatus));

        if (!string.IsNullOrEmpty(SessionSearchText))
        {
            var search = SessionSearchText.ToLowerInvariant();
            filtered = filtered.Where(s =>
                s.Login.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.Host.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.CurrentStatement.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.QueryText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(SessionDatabaseFilter) && SessionDatabaseFilter != "(All)")
            filtered = filtered.Where(s => s.Database == SessionDatabaseFilter);

        Dispatcher.UIThread.Post(() =>
        {
            Sessions.Clear();
            foreach (var s in filtered) Sessions.Add(s);
        });
    }

    private void ApplyJobFilters()
    {
        var filtered = _allJobs.AsEnumerable();

        if (!string.IsNullOrEmpty(JobSearchText))
            filtered = filtered.Where(j => j.JobName.Contains(JobSearchText, StringComparison.OrdinalIgnoreCase));

        if (JobEnabledFilter == "Enabled only")
            filtered = filtered.Where(j => j.IsEnabled);
        else if (JobEnabledFilter == "Disabled only")
            filtered = filtered.Where(j => !j.IsEnabled);

        if (JobOutcomeFilter == "Failed only")
            filtered = filtered.Where(j => j.LastRunOutcome == "Failed");
        else if (JobOutcomeFilter == "Succeeded only")
            filtered = filtered.Where(j => j.LastRunOutcome == "Succeeded");

        if (!string.IsNullOrEmpty(JobCategoryFilter) && JobCategoryFilter != "(All)")
            filtered = filtered.Where(j => j.Category == JobCategoryFilter);

        if (ShowOnlyRunning)
            filtered = filtered.Where(j => j.CurrentStatus == "Executing");

        Dispatcher.UIThread.Post(() =>
        {
            Jobs.Clear();
            foreach (var j in filtered) Jobs.Add(j);
        });
    }

    // ── Blocking Chain Detection ──────────────────────────────────

    private void UpdateBlockingChains()
    {
        var blockers = _allSessions
            .Where(s => s.BlockingSession > 0)
            .ToList();

        if (blockers.Count == 0)
        {
            HasBlockingChains = false;
            BlockingChainText = "";
            return;
        }

        // Build chains: find the head of each blocking chain
        var chains = new List<string>();
        var visited = new HashSet<int>();

        foreach (var session in blockers)
        {
            if (visited.Contains(session.SessionId)) continue;

            // Walk up to find the head blocker
            var head = session.BlockingSession;
            var chainIds = new List<int> { session.SessionId };
            visited.Add(session.SessionId);

            // Walk down from head to find all blocked sessions
            var blocked = _allSessions.Where(s => s.BlockingSession == head).Select(s => s.SessionId).ToList();
            var chain = $"Session {head} \u2192 blocks \u2192 {string.Join(", ", blocked.Select(id => $"Session {id}"))}";
            chains.Add(chain);
        }

        HasBlockingChains = true;
        BlockingChainText = string.Join("  |  ", chains);
    }

    // ── Session Actions ───────────────────────────────────────────

    public bool CanKillSession(SessionRow? session)
        => session != null && !session.IsCurrentSession;

    [RelayCommand]
    public async Task KillSessionAsync(SessionRow? session)
    {
        if (session == null || _connectionString == null || session.IsCurrentSession) return;

        try
        {
            await _db.KillSessionAsync(_connectionString, session.SessionId);
            ActionCompleted?.Invoke($"Killed session {session.SessionId} ({session.Login}) at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to kill session {session.SessionId}: {ex.Message}");
        }
    }

    public void CopyQueryText(SessionRow? session)
    {
        // Handled in code-behind (clipboard requires UI thread + TopLevel)
    }

    public void OpenQueryInEditor(SessionRow? session)
    {
        if (session == null) return;
        var text = !string.IsNullOrWhiteSpace(session.QueryText) ? session.QueryText : session.CurrentStatement;
        if (!string.IsNullOrWhiteSpace(text))
            OpenInEditorRequested?.Invoke(text);
    }

    // ── Job Actions ───────────────────────────────────────────────

    [RelayCommand]
    public async Task StartJobAsync(JobRow? job)
    {
        if (job == null || _connectionString == null) return;

        try
        {
            await _db.StartJobAsync(_connectionString, job.JobName);
            ActionCompleted?.Invoke($"Started job '{job.JobName}' at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to start job '{job.JobName}': {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StopJobAsync(JobRow? job)
    {
        if (job == null || _connectionString == null) return;

        try
        {
            await _db.StopJobAsync(_connectionString, job.JobName);
            ActionCompleted?.Invoke($"Stopped job '{job.JobName}' at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to stop job '{job.JobName}': {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ToggleJobEnabledAsync(JobRow? job)
    {
        if (job == null || _connectionString == null) return;

        try
        {
            var newEnabled = !job.IsEnabled;
            await _db.EnableDisableJobAsync(_connectionString, job.JobName, newEnabled);
            ActionCompleted?.Invoke($"{(newEnabled ? "Enabled" : "Disabled")} job '{job.JobName}' at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to toggle job '{job.JobName}': {ex.Message}");
        }
    }

    // ── Job Detail Panel ──────────────────────────────────────────

    private JobRow? _previousSelectedJob;

    partial void OnSelectedJobChanged(JobRow? value)
    {
        if (value == null) return;

        // Toggle collapse if clicking the same job
        if (_previousSelectedJob == value && IsJobDetailVisible)
        {
            IsJobDetailVisible = false;
            _previousSelectedJob = null;
            return;
        }

        _previousSelectedJob = value;
        IsJobDetailVisible = true;
        HasUnsavedChanges = false;
        _ = LoadJobDetailAsync(value);
    }

    public void CollapseDetail()
    {
        IsJobDetailVisible = false;
        _previousSelectedJob = null;
    }

    partial void OnSelectedStepChanged(JobStepRow? value)
    {
        EditStepCommand = value?.Command ?? "";
    }

    private async Task LoadJobDetailAsync(JobRow job)
    {
        if (_connectionString == null) return;

        try
        {
            // Populate General tab
            EditJobName = job.JobName;
            EditJobDescription = job.Description;
            EditJobEnabled = job.IsEnabled;
            EditJobCategory = job.Category;

            // Load categories
            var cats = await _db.GetJobCategoriesAsync(_connectionString);
            Dispatcher.UIThread.Post(() =>
            {
                AllCategories.Clear();
                foreach (var c in cats) AllCategories.Add(c.Name);
            });

            // Load steps (detailed with OnSuccess/OnFailure)
            var steps = await _db.GetJobStepsDetailedAsync(_connectionString, job.JobName);
            Dispatcher.UIThread.Post(() =>
            {
                JobSteps.Clear();
                foreach (var s in steps)
                    JobSteps.Add(new JobStepRow
                    {
                        StepId = s.StepId,
                        StepName = s.StepName,
                        Subsystem = s.Subsystem,
                        Command = s.Command,
                        OnSuccessAction = s.OnSuccessAction,
                        OnFailureAction = s.OnFailureAction
                    });
                SelectedStep = null;
                EditStepCommand = "";
            });

            // Load schedule
            var sched = await _db.GetJobScheduleAsync(_connectionString, job.JobName);
            if (sched.HasValue)
            {
                var s = sched.Value;
                HasSchedule = true;
                EditScheduleId = s.ScheduleId;
                EditFreqType = s.FreqType;
                EditFreqInterval = s.FreqInterval;
                EditFreqSubdayType = s.FreqSubdayType;
                EditFreqSubdayInterval = s.FreqSubdayInterval;
                EditActiveStartHour = s.ActiveStartTime / 10000;
                EditActiveStartMinute = (s.ActiveStartTime / 100) % 100;

                // Decode weekly bitmask
                if (s.FreqType == 8)
                {
                    SchedSun = (s.FreqInterval & 1) != 0;
                    SchedMon = (s.FreqInterval & 2) != 0;
                    SchedTue = (s.FreqInterval & 4) != 0;
                    SchedWed = (s.FreqInterval & 8) != 0;
                    SchedThu = (s.FreqInterval & 16) != 0;
                    SchedFri = (s.FreqInterval & 32) != 0;
                    SchedSat = (s.FreqInterval & 64) != 0;
                }

                UpdateSchedulePreview();
            }
            else
            {
                HasSchedule = false;
                EditScheduleId = 0;
                EditFreqType = 4;
                EditFreqInterval = 1;
                EditFreqSubdayType = 1;
                EditFreqSubdayInterval = 1;
                EditActiveStartHour = 0;
                EditActiveStartMinute = 0;
                SchedulePreview = "No schedule";
            }

            // Load history
            var history = await _db.GetJobHistoryAsync(_connectionString, job.JobName);
            Dispatcher.UIThread.Post(() =>
            {
                JobHistory.Clear();
                foreach (var h in history)
                    JobHistory.Add(new JobHistoryRow
                    {
                        RunStatus = h.RunStatus,
                        RunStatusDisplay = h.RunStatus switch
                        {
                            0 => "Failed", 1 => "Succeeded", 2 => "Retry",
                            3 => "Cancelled", _ => "Unknown"
                        },
                        RunDate = h.RunDate,
                        DurationDisplay = JobScheduleFormatter.FormatDuration(h.DurationSeconds),
                        Message = h.Message
                    });
            });

            HasUnsavedChanges = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading job detail: {ex.Message}";
        }
    }

    // Mark unsaved on edits
    partial void OnEditJobNameChanged(string value) => HasUnsavedChanges = true;
    partial void OnEditJobDescriptionChanged(string value) => HasUnsavedChanges = true;
    partial void OnEditJobEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnEditJobCategoryChanged(string value) => HasUnsavedChanges = true;

    // ── General Tab Save ──────────────────────────────────────────

    [RelayCommand]
    public async Task SaveJobGeneralAsync()
    {
        if (SelectedJob == null || _connectionString == null) return;
        var originalName = SelectedJob.JobName;

        try
        {
            // Resolve category ID
            int? catId = null;
            if (!string.IsNullOrEmpty(EditJobCategory))
            {
                var cats = await _db.GetJobCategoriesAsync(_connectionString);
                var match = cats.FirstOrDefault(c => c.Name == EditJobCategory);
                if (match != default) catId = match.CategoryId;
            }

            await _db.UpdateJobAsync(_connectionString, originalName,
                newName: EditJobName != originalName ? EditJobName : null,
                description: EditJobDescription,
                enabled: EditJobEnabled,
                categoryId: catId);

            HasUnsavedChanges = false;
            ActionCompleted?.Invoke($"Saved job '{EditJobName}' at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();

            // Re-select the job (name may have changed)
            SelectedJob = Jobs.FirstOrDefault(j => j.JobName == EditJobName);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to save job: {ex.Message}");
        }
    }

    // ── Steps Tab Actions ─────────────────────────────────────────

    [RelayCommand]
    public async Task SaveStepAsync()
    {
        if (SelectedJob == null || SelectedStep == null || _connectionString == null) return;

        try
        {
            await _db.UpdateJobStepAsync(_connectionString, SelectedJob.JobName,
                SelectedStep.StepId, SelectedStep.StepName, EditStepCommand);
            ActionCompleted?.Invoke($"Saved step '{SelectedStep.StepName}' at {DateTime.Now:HH:mm:ss}");
            await LoadJobDetailAsync(SelectedJob);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to save step: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task AddStepAsync()
    {
        if (SelectedJob == null || _connectionString == null) return;

        try
        {
            var stepName = $"New Step {JobSteps.Count + 1}";
            await _db.AddJobStepAsync(_connectionString, SelectedJob.JobName,
                stepName, "TSQL", "-- Enter command here", "master");
            ActionCompleted?.Invoke($"Added step '{stepName}' at {DateTime.Now:HH:mm:ss}");
            await LoadJobDetailAsync(SelectedJob);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to add step: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DeleteStepAsync()
    {
        if (SelectedJob == null || SelectedStep == null || _connectionString == null) return;

        try
        {
            await _db.DeleteJobStepAsync(_connectionString, SelectedJob.JobName, SelectedStep.StepId);
            ActionCompleted?.Invoke($"Deleted step '{SelectedStep.StepName}' at {DateTime.Now:HH:mm:ss}");
            await LoadJobDetailAsync(SelectedJob);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to delete step: {ex.Message}");
        }
    }

    public void OpenStepInEditor(JobStepRow? step)
    {
        if (step != null && !string.IsNullOrWhiteSpace(step.Command))
            OpenInEditorRequested?.Invoke(step.Command);
    }

    // ── Schedule Tab Actions ──────────────────────────────────────

    private int BuildWeeklyBitmask()
    {
        int mask = 0;
        if (SchedSun) mask |= 1;
        if (SchedMon) mask |= 2;
        if (SchedTue) mask |= 4;
        if (SchedWed) mask |= 8;
        if (SchedThu) mask |= 16;
        if (SchedFri) mask |= 32;
        if (SchedSat) mask |= 64;
        return mask == 0 ? 1 : mask; // default to Sunday if nothing selected
    }

    private int BuildActiveStartTime() => EditActiveStartHour * 10000 + EditActiveStartMinute * 100;

    private int GetEffectiveFreqInterval()
    {
        if (EditFreqType == 8) return BuildWeeklyBitmask();
        return EditFreqInterval;
    }

    public void UpdateSchedulePreview()
    {
        SchedulePreview = JobScheduleFormatter.Format(
            EditFreqType, GetEffectiveFreqInterval(),
            EditFreqSubdayType, EditFreqSubdayInterval,
            BuildActiveStartTime());
    }

    // Update preview when schedule params change
    partial void OnEditFreqTypeChanged(int value) => UpdateSchedulePreview();
    partial void OnEditFreqIntervalChanged(int value) => UpdateSchedulePreview();
    partial void OnEditFreqSubdayTypeChanged(int value) => UpdateSchedulePreview();
    partial void OnEditFreqSubdayIntervalChanged(int value) => UpdateSchedulePreview();
    partial void OnEditActiveStartHourChanged(int value) => UpdateSchedulePreview();
    partial void OnEditActiveStartMinuteChanged(int value) => UpdateSchedulePreview();
    partial void OnSchedSunChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedMonChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedTueChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedWedChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedThuChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedFriChanged(bool value) => UpdateSchedulePreview();
    partial void OnSchedSatChanged(bool value) => UpdateSchedulePreview();

    [RelayCommand]
    public async Task SaveScheduleAsync()
    {
        if (SelectedJob == null || _connectionString == null) return;

        try
        {
            var freqInterval = GetEffectiveFreqInterval();
            var startTime = BuildActiveStartTime();

            if (HasSchedule && EditScheduleId > 0)
            {
                await _db.UpdateJobScheduleAsync(_connectionString, SelectedJob.JobName,
                    EditScheduleId, EditFreqType, freqInterval,
                    EditFreqSubdayType, EditFreqSubdayInterval, startTime);
                ActionCompleted?.Invoke($"Updated schedule at {DateTime.Now:HH:mm:ss}");
            }
            else
            {
                await _db.AddJobScheduleAsync(_connectionString, SelectedJob.JobName,
                    $"Schedule_{SelectedJob.JobName}", EditFreqType, freqInterval,
                    EditFreqSubdayType, EditFreqSubdayInterval, startTime);
                HasSchedule = true;
                ActionCompleted?.Invoke($"Added schedule at {DateTime.Now:HH:mm:ss}");
            }

            await RefreshAsync();
            if (SelectedJob != null) await LoadJobDetailAsync(SelectedJob);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to save schedule: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RemoveScheduleAsync()
    {
        if (SelectedJob == null || _connectionString == null || !HasSchedule) return;

        try
        {
            await _db.DeleteJobScheduleAsync(_connectionString, SelectedJob.JobName, EditScheduleId);
            HasSchedule = false;
            ActionCompleted?.Invoke($"Removed schedule at {DateTime.Now:HH:mm:ss}");
            await RefreshAsync();
            if (SelectedJob != null) await LoadJobDetailAsync(SelectedJob);
        }
        catch (Exception ex)
        {
            ActionCompleted?.Invoke($"Failed to remove schedule: {ex.Message}");
        }
    }

    // ── Auto-Refresh ──────────────────────────────────────────────

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        if (value)
            StartAutoRefresh();
        else
            StopAutoRefresh();
    }

    partial void OnAutoRefreshIntervalSecondsChanged(int value)
    {
        if (AutoRefreshEnabled)
        {
            StopAutoRefresh();
            StartAutoRefresh();
        }
    }

    private void StartAutoRefresh()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AutoRefreshIntervalSeconds)
        };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();
        _autoRefreshTimer.Start();
    }

    private void StopAutoRefresh()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = null;
    }

    // ── Sub-tab switching ─────────────────────────────────────────

    partial void OnIsSessionsTabActiveChanged(bool value)
    {
        if (value)
        {
            IsJobsTabActive = false;
            _ = RefreshAsync();
        }
    }

    partial void OnIsJobsTabActiveChanged(bool value)
    {
        if (value)
        {
            IsSessionsTabActive = false;
            _ = RefreshAsync();
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────

    public void Dispose()
    {
        StopAutoRefresh();
    }
}

// ── Row Models ────────────────────────────────────────────────────

public class SessionRow
{
    public int SessionId { get; set; }
    public string Login { get; set; } = "";
    public string Database { get; set; } = "";
    public string SessionStatus { get; set; } = "";
    public string RequestStatus { get; set; } = "";
    public string Command { get; set; } = "";
    public string WaitType { get; set; } = "";
    public int WaitTimeMs { get; set; }
    public int BlockingSession { get; set; }
    public long CpuMs { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long LogicalReads { get; set; }
    public int ElapsedSeconds { get; set; }
    public double PercentComplete { get; set; }
    public int OpenTransactions { get; set; }
    public string Host { get; set; } = "";
    public string Program { get; set; } = "";
    public string QueryText { get; set; } = "";
    public string CurrentStatement { get; set; } = "";
    public bool IsCurrentSession { get; set; }

    public bool IsBlocking => BlockingSession > 0;
    public string ElapsedDisplay => ElapsedSeconds > 0 ? JobScheduleFormatter.FormatDuration(ElapsedSeconds) : "";
    public string CurrentStatementPreview => CurrentStatement.Length > 200
        ? CurrentStatement[..200] + "..."
        : CurrentStatement;
}

public class JobRow
{
    public string JobName { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string Category { get; set; } = "";
    public string CurrentStatus { get; set; } = "Idle";
    public string LastRunOutcome { get; set; } = "Unknown";
    public DateTime? LastRunDate { get; set; }
    public int LastDurationSeconds { get; set; }
    public string LastDurationDisplay { get; set; } = "";
    public DateTime? NextRunDate { get; set; }
    public string Schedule { get; set; } = "";
    public string Description { get; set; } = "";
    public int StepCount { get; set; }

    public bool IsExecuting => CurrentStatus == "Executing";
    public string EnabledDisplay => IsEnabled ? "Yes" : "No";
}

public class JobStepRow
{
    public int StepId { get; set; }
    public string StepName { get; set; } = "";
    public string Subsystem { get; set; } = "";
    public string Command { get; set; } = "";
    public string OnSuccessAction { get; set; } = "";
    public string OnFailureAction { get; set; } = "";
    public string CommandPreview => Command.Length > 100 ? Command[..100] + "..." : Command;
}

public class JobHistoryRow
{
    public int RunStatus { get; set; }
    public string RunStatusDisplay { get; set; } = "";
    public DateTime RunDate { get; set; }
    public string DurationDisplay { get; set; } = "";
    public string Message { get; set; } = "";
}
