using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Invicta.Time.Native.Kernel32;
using static Invicta.Time.Native.Winmm;

namespace Invicta.Time;

internal sealed class WaitableTimerHighResolutionTimer : ITimer
{
    private record Context(TimerCallback Callback, object? State);

    private bool _disposed;

    private readonly GCHandle _contextHandle;

    private readonly nint _timer;

    public WaitableTimerHighResolutionTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime.TotalMilliseconds, int.MaxValue);

        ArgumentOutOfRangeException.ThrowIfLessThan(period.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period.TotalMilliseconds, int.MaxValue);

        Context context = new(callback, state);
        _contextHandle = GCHandle.Alloc(context);

        if (timeBeginPeriod(1) != TIMERR_NOERROR)
        {
            throw new PlatformNotSupportedException("Failed to set minimum resolution for periodic timers.");
        }

        _timer = CreateWaitableTimerExW(
            nint.Zero,
            nint.Zero,
            0,
            TIMER_ALL_ACCESS
        );

        if (_timer == nint.Zero)
        {
            throw new Win32Exception();
        }

        bool result = Change(dueTime, period);

        if (!result)
        {
            throw new Win32Exception();
        }
    }

    public unsafe bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime.TotalMilliseconds, 4294967294);

        ArgumentOutOfRangeException.ThrowIfLessThan(period.TotalMilliseconds, Timeout.Infinite);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period.TotalMilliseconds, 4294967294);

        return SetWaitableTimer(_timer, -dueTime.Ticks, (int)period.TotalMilliseconds, &TimerApcRoutine, GCHandle.ToIntPtr(_contextHandle), false);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TimerApcRoutine(nint lpArgToCompletionRoutine, uint dwTimerLowValue, uint dwTimerHighValue)
    {
        GCHandle contextHandle = GCHandle.FromIntPtr(lpArgToCompletionRoutine);
        (TimerCallback callback, object? state) = (Context)contextHandle.Target!;

        callback(state);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
                _contextHandle.Free();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            if (timeEndPeriod(1) != TIMERR_NOERROR)
            {
                throw new PlatformNotSupportedException("Failed to clear minimum resolution for periodic timers.");
            }

            // TODO: set large fields to null
            _disposed = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    ~WaitableTimerHighResolutionTimer()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}
