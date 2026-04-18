using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVersionControl.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public enum TraceState { Setup, Recording, Results }

public partial class TraceViewModel : ObservableObject
{
    private readonly TraceService _traceService = new();
    private string? _connectionString;
    private string? _activeSessionName;
    private BackgroundPollManager? _pollManager;
    private string? _pollerId;
    private DateTime _startTime;

    // ── State ────────────────────────────────────────────────────────

    [ObservableProperty] private TraceState _state = TraceState.Setup;
    [ObservableProperty] private string _statusMessage = "Configure filters and start capture.";

    // ── Filter Setup ─────────────────────────────────────────────────

    [ObservableProperty] private string _filterDatabase = "";
    [ObservableProperty] private string _filterLogin = "";
    [ObservableProperty] private string _filterAppName = "";
    [ObservableProperty] private int _filterMinDurationMs;
    [ObservableProperty] private string _filterTextContains = "";

    // ── Recording State ──────────────────────────────────────────────

    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private int _capturedCount;

    // ── Results ──────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<TraceEvent> _events = [];
    [ObservableProperty] private ObservableCollection<TraceEvent> _filteredEvents = [];
    [ObservableProperty] private TraceEvent? _selectedEvent;
    [ObservableProperty] private string _searchText = "";

    // ── Connection ───────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _connectionNames = [];
    [ObservableProperty] private string? _selectedConnectionName;

    private readonly Dictionary<string, string> _connectionMap = new(); // name → connStr

    public void SetConnections(ConnectionRegistry registry)
    {
        ConnectionNames.Clear();
        _connectionMap.Clear();

        foreach (var managed in registry.ActiveConnections)
        {
            var name = managed.DisplayName;
            ConnectionNames.Add(name);
            if (managed.ResolvedConnectionString != null)
                _connectionMap[name] = managed.ResolvedConnectionString;
        }

        if (ConnectionNames.Count > 0)
            SelectedConnectionName = ConnectionNames[0];
    }

    public void SetPollManager(BackgroundPollManager manager)
    {
        _pollManager = manager;
    }

    private async Task ReadAndUpdateCounterAsync()
    {
        if (_activeSessionName == null || _connectionString == null) return;
        var events = await _traceService.ReadEventsAsync(_connectionString, _activeSessionName);
        CapturedCount = events.Count;
        var elapsed = DateTime.Now - _startTime;
        ElapsedText = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    partial void OnSelectedConnectionNameChanged(string? value)
    {
        if (value != null && _connectionMap.TryGetValue(value, out var connStr))
            _connectionString = connStr;
    }

    /// <summary>Set connection string directly (from ConnectionIndicator).</summary>
    public void SetConnectionDirect(string connectionString)
    {
        _connectionString = connectionString;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch();
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            StatusMessage = "No connection selected.";
            return;
        }

        // Permission check
        if (!await _traceService.HasTracePermissionAsync(_connectionString))
        {
            StatusMessage = "Requires ALTER ANY EVENT SESSION permission.";
            return;
        }

        var options = new TraceOptions
        {
            CaptureStatements = false, // Mode 3: batch + rpc completed
            DatabaseFilter = string.IsNullOrWhiteSpace(FilterDatabase) ? null : FilterDatabase.Trim(),
            LoginFilter = string.IsNullOrWhiteSpace(FilterLogin) ? null : FilterLogin.Trim(),
            AppNameFilter = string.IsNullOrWhiteSpace(FilterAppName) ? null : FilterAppName.Trim(),
            MinDurationMs = FilterMinDurationMs,
            TextContains = string.IsNullOrWhiteSpace(FilterTextContains) ? null : FilterTextContains.Trim(),
        };

        try
        {
            _activeSessionName = await _traceService.StartTraceAsync(_connectionString, options);
            State = TraceState.Recording;
            StatusMessage = "Recording...";
            CapturedCount = 0;
            _startTime = DateTime.Now;

            // Poll counter every 5 seconds via BackgroundPollManager (NeverGated — runs always while recording)
            if (_pollManager != null)
            {
                _pollerId = _pollManager.Register(
                    owningTab: "Trace",
                    interval: TimeSpan.FromSeconds(5),
                    tick: ReadAndUpdateCounterAsync,
                    gating: PollerGating.NeverGated,
                    label: "recording");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        if (_pollManager != null && _pollerId != null)
        {
            _pollManager.Unregister(_pollerId);
            _pollerId = null;
        }

        if (_activeSessionName == null || _connectionString == null)
        {
            State = TraceState.Setup;
            return;
        }

        StatusMessage = "Reading trace data...";

        try
        {
            // Read final events before stopping
            var events = await _traceService.ReadEventsAsync(_connectionString, _activeSessionName);

            // Apply client-side filters (login, app name, text contains)
            var filtered = events.Where(e => PassesClientFilter(e)).ToList();

            Events.Clear();
            foreach (var e in filtered)
                Events.Add(e);

            ApplySearch();

            StatusMessage = $"Captured {Events.Count} event(s) in {ElapsedText}.";
            State = TraceState.Results;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error reading trace: {ex.Message}";
            State = TraceState.Results;
        }
        finally
        {
            // Always cleanup the XE session
            await _traceService.StopTraceAsync(_connectionString, _activeSessionName);
            _activeSessionName = null;
        }
    }

    [RelayCommand]
    private void ResetToSetup()
    {
        Events.Clear();
        FilteredEvents.Clear();
        SelectedEvent = null;
        SearchText = "";
        CapturedCount = 0;
        ElapsedText = "00:00";
        State = TraceState.Setup;
        StatusMessage = "Configure filters and start capture.";
    }

    // ── Client-side Filtering ────────────────────────────────────────

    private bool PassesClientFilter(TraceEvent e)
    {
        if (!string.IsNullOrWhiteSpace(FilterLogin) &&
            !e.Login.Contains(FilterLogin.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(FilterAppName) &&
            !e.Application.Contains(FilterAppName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(FilterTextContains) &&
            !e.SqlText.Contains(FilterTextContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void ApplySearch()
    {
        var search = SearchText?.Trim() ?? "";

        FilteredEvents.Clear();
        foreach (var e in Events)
        {
            if (string.IsNullOrEmpty(search) ||
                e.SqlText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Database.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Login.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Host.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Application.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredEvents.Add(e);
            }
        }
    }
}
