using Avalonia.Threading;

namespace SqlVersionControl.Services;

public enum PollerGating
{
    /// <summary>Pauses on tab-inactive AND window-unfocused. For passive "keep it fresh" polling.</summary>
    FullyGated,

    /// <summary>Runs as long as registered. For user-initiated captures where pausing causes data loss.</summary>
    NeverGated,
}

public record PollManagerState(bool IsAnyPolling, string? ActivePollerLabel, int PollerCount);

public class BackgroundPollManager
{
    private readonly Dictionary<string, PollerEntry> _pollers = new();
    private bool _isWindowFocused = true;
    private string? _activeTab;
    private int _nextId;

    public event Action<PollManagerState>? StateChanged;
    public PollManagerState CurrentState { get; private set; } = new(false, null, 0);

    public string Register(string owningTab, TimeSpan interval, Func<Task> tick,
        PollerGating gating = PollerGating.FullyGated, string? label = null)
    {
        var id = $"poller_{_nextId++}";
        var entry = new PollerEntry
        {
            Id = id,
            OwningTab = owningTab,
            Tick = tick,
            Gating = gating,
            Label = label ?? owningTab.ToLowerInvariant(),
            Enabled = true,
        };

        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += async (_, _) => await ExecuteTick(entry);
        entry.Timer = timer;

        _pollers[id] = entry;

        if (ShouldTick(entry))
            timer.Start();

        UpdateState();
        return id;
    }

    public void Unregister(string id)
    {
        if (!_pollers.TryGetValue(id, out var entry)) return;
        entry.Timer.Stop();
        entry.Disposed = true;
        _pollers.Remove(id);
        UpdateState();
    }

    public void SetPollerEnabled(string id, bool enabled)
    {
        if (!_pollers.TryGetValue(id, out var entry)) return;
        entry.Enabled = enabled;
        ReEvaluatePoller(entry);
        UpdateState();
    }

    public void SetPollerInterval(string id, TimeSpan interval)
    {
        if (!_pollers.TryGetValue(id, out var entry)) return;
        var wasRunning = entry.Timer.IsEnabled;
        entry.Timer.Stop();
        entry.Timer.Interval = interval;
        if (wasRunning)
            entry.Timer.Start();
    }

    public void SetWindowFocused(bool focused)
    {
        if (_isWindowFocused == focused) return;
        _isWindowFocused = focused;
        ReEvaluateAll(catchUpOnResume: focused);
        UpdateState();
    }

    public void SetActiveTab(string? tabName)
    {
        if (_activeTab == tabName) return;
        _activeTab = tabName;
        ReEvaluateAll(catchUpOnResume: true);
        UpdateState();
    }

    private bool ShouldTick(PollerEntry entry)
    {
        if (!entry.Enabled) return false;
        if (entry.Gating == PollerGating.NeverGated) return true;

        // FullyGated: must have window focus AND matching tab
        return _isWindowFocused && _activeTab == entry.OwningTab;
    }

    private void ReEvaluatePoller(PollerEntry entry, bool catchUpOnResume = false)
    {
        var shouldRun = ShouldTick(entry);
        var isRunning = entry.Timer.IsEnabled;

        if (shouldRun && !isRunning)
        {
            // Resuming — fire immediate catch-up then restart periodic tick
            if (catchUpOnResume)
                _ = ExecuteTick(entry);

            // Reset timer to full interval from now (prevents double-fire)
            entry.Timer.Stop();
            entry.Timer.Start();
        }
        else if (!shouldRun && isRunning)
        {
            entry.Timer.Stop();
        }
    }

    private void ReEvaluateAll(bool catchUpOnResume)
    {
        foreach (var entry in _pollers.Values)
            ReEvaluatePoller(entry, catchUpOnResume);
    }

    private async Task ExecuteTick(PollerEntry entry)
    {
        if (entry.Disposed) return;
        if (entry.IsTicking) return; // previous tick still in flight
        if (!entry.Enabled) return;

        entry.IsTicking = true;
        try
        {
            await entry.Tick();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"BackgroundPollManager.Tick[{entry.Id}]", ex);
        }
        finally
        {
            entry.IsTicking = false;
        }
    }

    private void UpdateState()
    {
        var activePollers = _pollers.Values.Where(p => p.Enabled && p.Timer.IsEnabled).ToList();
        var isAnyPolling = activePollers.Count > 0;
        string? label = null;

        if (isAnyPolling)
        {
            // Prefer showing "recording" for NeverGated pollers, else "polling"
            var activeEntry = activePollers.FirstOrDefault(p => p.Gating == PollerGating.NeverGated)
                              ?? activePollers[0];
            label = activeEntry.Gating == PollerGating.NeverGated
                ? "recording"
                : activeEntry.Label;
        }

        var newState = new PollManagerState(isAnyPolling, label, _pollers.Count);
        if (CurrentState != newState)
        {
            CurrentState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private class PollerEntry
    {
        public required string Id;
        public required string OwningTab;
        public required Func<Task> Tick;
        public required PollerGating Gating;
        public required string Label;
        public required bool Enabled;
        public DispatcherTimer Timer = null!;
        public bool IsTicking;
        public bool Disposed;
    }
}
