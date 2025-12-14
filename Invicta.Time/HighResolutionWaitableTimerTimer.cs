using System.Runtime.Versioning;

namespace Invicta.Time;

[SupportedOSPlatform("windows10.0.17134.0")]
internal sealed class HighResolutionWaitableTimerTimer : ITimer
{
    private bool _disposed;

    private readonly TimerCallback _callback;
    private readonly object? _state;

    private readonly WaitableTimer _timer;
    private readonly RegisteredWaitHandle _registeredTimer;

    public HighResolutionWaitableTimerTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        _callback = callback;
        _state = state;

        _timer = new WaitableTimer(WaitableTimerResetMode.AutoReset, WaitableTimerResolution.High);

        _registeredTimer = ThreadPool.RegisterWaitForSingleObject(
            _timer,
            OnTimerSignaled,
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false
        );

        _ = Change(dueTime, period);
    }

    private void OnTimerSignaled(object? state, bool timedOut)
    {
        _callback(_state);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime.TotalMilliseconds, int.MaxValue);

        ArgumentOutOfRangeException.ThrowIfLessThan(period.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period.TotalMilliseconds, int.MaxValue);

        return _timer.Set(dueTime, period);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _registeredTimer.Unregister(null);

                _timer.Cancel();
                _timer.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    ~HighResolutionWaitableTimerTimer()
    {
        Dispose(disposing: false);
    }
}
