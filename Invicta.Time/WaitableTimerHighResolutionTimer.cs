using System.ComponentModel;

using static Invicta.Time.Native.Kernel32;
using static Invicta.Time.Native.Winmm;

namespace Invicta.Time;

internal sealed class WaitableTimerHighResolutionTimer : ITimer
{
    private bool _disposed;

    private readonly TimerCallback _callback;
    private readonly object? _state;
    private readonly CancellationTokenSource _waitForTicksCts;
    private readonly Task _waitForTicksTask;

    private readonly nint _timer;

    public WaitableTimerHighResolutionTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        _callback = callback;
        _state = state;
        _waitForTicksCts = new CancellationTokenSource();

        if (timeBeginPeriod(1) != TIMERR_NOERROR)
        {
            throw new InvalidOperationException("Failed to set minimum resolution for periodic timers.");
        }

        _timer = CreateWaitableTimerExW(
            nint.Zero,
            nint.Zero,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS
        );

        if (_timer == nint.Zero)
        {
            throw new Win32Exception();
        }

        if (!Change(dueTime, period))
        {
            throw new Win32Exception();
        }

        _waitForTicksTask = Task.Run(WaitForTicks, _waitForTicksCts.Token);
    }

    public unsafe bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime.TotalMilliseconds, 4294967294);

        ArgumentOutOfRangeException.ThrowIfLessThan(period.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period.TotalMilliseconds, 4294967294);

        return SetWaitableTimerEx(_timer, -dueTime.Ticks, (int)period.TotalMilliseconds, null, nint.Zero, nint.Zero, 0);
    }

    private void WaitForTicks()
    {
        while (!_waitForTicksCts.IsCancellationRequested)
        {
            if (WaitForMultipleObjects(1, [_timer], true, 1) == WAIT_OBJECT_0)
            {
                _callback(_state);
            }
        }
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _waitForTicksCts.Cancel();
                _waitForTicksTask.Wait();

                _waitForTicksTask.Dispose();
                _waitForTicksCts.Dispose();
            }

            _ = CancelWaitableTimer(_timer);
            _ = CloseHandle(_timer);

            if (timeEndPeriod(1) != TIMERR_NOERROR)
            {
                throw new InvalidOperationException("Failed to clear minimum resolution for periodic timers.");
            }

            _disposed = true;
        }
    }

    ~WaitableTimerHighResolutionTimer()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _waitForTicksCts.CancelAsync();
        await _waitForTicksTask;

        _waitForTicksTask.Dispose();
        _waitForTicksCts.Dispose();

        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }
}
