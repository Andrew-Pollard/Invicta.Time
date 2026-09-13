// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

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

    /// <summary>Stops the timer once the caller has dropped every reference to it.</summary>
    ~HighResolutionTimer() => _entry.Close();

    /// <summary>Creates and starts a timer.</summary>
    /// <param name="callback">The method to invoke each time the timer fires.</param>
    /// <param name="state">The object to pass to <paramref name="callback"/>.</param>
    /// <param name="dueTime">
    /// The delay before the first callback, or <see cref="Timeout.InfiniteTimeSpan"/> to leave the timer stopped.
    /// </param>
    /// <param name="period">
    /// The interval between callbacks, or <see cref="Timeout.InfiniteTimeSpan"/> or zero for a one-shot timer.
    /// </param>
    /// <returns>The new timer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dueTime"/> or <paramref name="period"/> is negative and not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or is longer than 4294967294 milliseconds.
    /// </exception>
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

    /// <inheritdoc/>
    public bool Change(TimeSpan dueTime, TimeSpan period) =>
        _entry.Change(
            TimerEntry.ToStopwatchTicks(dueTime, nameof(dueTime)),
            TimerEntry.ToStopwatchTicks(period, nameof(period)));

    /// <inheritdoc/>
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
/// <param name="callback">The method to invoke each time the timer fires.</param>
/// <param name="state">The object to pass to <paramref name="callback"/>.</param>
/// <param name="executionContext">
/// The context to invoke <paramref name="callback"/> in, or <see langword="null"/> to invoke it in whatever context
/// the thread pool thread has.
/// </param>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class TimerEntry(TimerCallback callback, object? state, ExecutionContext? executionContext)
    : IThreadPoolWorkItem
{
    // Matches System.Threading.Timer's upper bound (0xFFFFFFFE ms, ~49.7 days).
    private const long MaxSupportedTimeoutMs = 0xFFFFFFFE;

    private static readonly ContextCallback s_invokeInContext = static s => ((TimerEntry)s!).Invoke();

    private static long s_lastSequence;

    private readonly TimerCallback _callback = callback;
    private readonly object? _state = state;
    private readonly ExecutionContext? _executionContext = executionContext;

    private readonly Lock _lock = new();

    // Guarded by _lock.
    private bool _closed;
    private int _callbacksRunning;
    private TaskCompletionSource? _closeCompletion;

    /// <summary>Gets a number unique to this entry, which orders entries that are due at the same time.</summary>
    internal long Sequence { get; } = Interlocked.Increment(ref s_lastSequence);

    /// <summary>Gets or sets the <see cref="Stopwatch"/> timestamp at which the timer is next due.</summary>
    /// <remarks>
    /// Guarded by the <see cref="TimerScheduler"/> lock. Only change it while the entry is not scheduled, because the
    /// scheduler keeps its entries sorted by this value.
    /// </remarks>
    internal long DueTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the number of <see cref="Stopwatch"/> ticks between callbacks, or zero for a one-shot timer.
    /// </summary>
    /// <remarks>Guarded by the <see cref="TimerScheduler"/> lock.</remarks>
    internal long PeriodTicks { get; set; }

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

    /// <summary>Reschedules the timer, unless it has been closed.</summary>
    /// <param name="dueTicks">
    /// <see cref="Stopwatch"/> ticks until the first callback, or -1 to stop the timer.
    /// </param>
    /// <param name="periodTicks">
    /// <see cref="Stopwatch"/> ticks between callbacks, or 0 or -1 for a one-shot timer.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the timer was rescheduled; <see langword="false"/> if it has been closed.
    /// </returns>
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

    /// <summary>
    /// Stops the timer. Callbacks that are queued but have not started are skipped; callbacks already running are
    /// not waited for.
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            CloseCore();
        }
    }

    /// <summary>Stops the timer, as <see cref="Close"/> does.</summary>
    /// <returns>A task that completes once any callbacks already running have finished.</returns>
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

    /// <summary>Marks the timer closed and removes it from the scheduler, if it is not already closed.</summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void CloseCore()
    {
        if (!_closed)
        {
            _closed = true;
            TimerScheduler.Instance.Unschedule(this);
        }
    }

    /// <summary>Invokes the callback on a thread pool thread, unless the timer has been closed.</summary>
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

    /// <summary>Invokes the callback with its state.</summary>
    private void Invoke() => _callback(_state);
}
