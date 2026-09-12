// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Invicta.Threading;

/// <summary>
/// The <see cref="ITimer"/> handed to callers. The scheduler only holds <see cref="TimerEntry"/>, so if the
/// caller drops every reference to this wrapper it is collected and its finalizer stops the timer, matching
/// <see cref="System.Threading.Timer"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class HighResolutionTimer : ITimer
{
    private readonly TimerEntry _entry;

    private HighResolutionTimer(TimerEntry entry) => _entry = entry;

    ~HighResolutionTimer() => _entry.Close();

    public static HighResolutionTimer Create(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Everything that can throw happens before the finalizable wrapper is allocated; otherwise a
        // half-constructed instance would reach the finalizer with a null _entry and crash the process.
        long due = TimerEntry.ToStopwatchTicks(dueTime, nameof(dueTime));
        long per = TimerEntry.ToStopwatchTicks(period, nameof(period));
        _ = TimerScheduler.Instance;

        TimerEntry entry = new(callback, state, ExecutionContext.Capture());
        entry.Change(due, per);
        return new HighResolutionTimer(entry);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period) =>
        _entry.Change(
            TimerEntry.ToStopwatchTicks(dueTime, nameof(dueTime)),
            TimerEntry.ToStopwatchTicks(period, nameof(period)));

    public void Dispose()
    {
        _entry.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the timer and completes once any callbacks already running have finished.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _entry.CloseAsync();
    }
}

/// <summary>
/// A scheduled timer. Scheduling state is guarded by the <see cref="TimerScheduler"/> lock; lifetime state by
/// the entry's own lock.
/// </summary>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class TimerEntry(TimerCallback callback, object? state, ExecutionContext? executionContext)
    : IThreadPoolWorkItem
{
    // Matches System.Threading.Timer's upper bound (0xFFFFFFFE ms, ~49.7 days).
    private const long MaxSupportedTimeoutMs = 0xFFFFFFFE;

    private static readonly ContextCallback s_invokeInContext = static s => ((TimerEntry)s!).Invoke();

    private readonly TimerCallback _callback = callback;
    private readonly object? _state = state;
    private readonly ExecutionContext? _executionContext = executionContext;

    private readonly Lock _lock = new();

    // Guarded by _lock.
    private bool _closed;
    private int _callbacksRunning;
    private TaskCompletionSource? _closeCompletion;

    // Guarded by TimerScheduler's lock.
    internal long DueTimestamp { get; set; }

    internal long PeriodTicks { get; set; }

    internal int HeapIndex { get; set; } = -1;

    /// <summary>Converts to <see cref="Stopwatch"/> ticks; -1 means infinite.</summary>
    internal static long ToStopwatchTicks(TimeSpan value, string paramName)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return -1;
        }

        if (value < TimeSpan.Zero || value.Ticks / TimeSpan.TicksPerMillisecond > MaxSupportedTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "The value must be Timeout.InfiniteTimeSpan or between 0 and 4294967294 milliseconds.");
        }

        return Stopwatch.Frequency == TimeSpan.TicksPerSecond
            ? value.Ticks
            : (long)((Int128)value.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }

    public bool Change(long dueTicks, long periodTicks)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            TimerScheduler.Instance.Schedule(this, dueTicks, periodTicks);
            return true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            CloseCore();
        }
    }

    public ValueTask CloseAsync()
    {
        lock (_lock)
        {
            CloseCore();
            if (_callbacksRunning == 0)
            {
                return default;
            }

            _closeCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_closeCompletion.Task);
        }
    }

    private void CloseCore()
    {
        if (!_closed)
        {
            _closed = true;
            TimerScheduler.Instance.Unschedule(this);
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        lock (_lock)
        {
            // A callback queued just before Close() is skipped rather than run after disposal.
            if (_closed)
            {
                return;
            }

            _callbacksRunning++;
        }

        try
        {
            if (_executionContext is null)
            {
                Invoke();
            }
            else
            {
                ExecutionContext.Run(_executionContext, s_invokeInContext, this);
            }
        }
        finally
        {
            lock (_lock)
            {
                if (--_callbacksRunning == 0 && _closed)
                {
                    _closeCompletion?.TrySetResult();
                }
            }
        }
    }

    private void Invoke() => _callback(_state);
}
