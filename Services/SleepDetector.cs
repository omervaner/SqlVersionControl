using Avalonia.Threading;

namespace SqlVersionControl.Services;

public class SleepDetector
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastTick;
    private readonly TimeSpan _sleepThreshold = TimeSpan.FromMinutes(2);

    public event Action? WokeFromSleep;

    public SleepDetector()
    {
        _lastTick = DateTime.UtcNow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (elapsed > _sleepThreshold)
        {
            WokeFromSleep?.Invoke();
        }
    }
}
