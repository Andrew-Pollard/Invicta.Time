// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
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
    // Matches System.Threading.Timer's upper bound (0xFFFFFFFE ms, ~49.7 days).
    private const long MaxSupportedTimeoutMs = 0xFFFFFFFE;

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
        ThrowIfInvalidTimeout(dueTime);
        ThrowIfInvalidTimeout(period);
        _ = TimerScheduler.Instance;

        TimerEntry entry = new(callback, state, ExecutionContext.Capture());
        entry.Change(dueTime, period);

        return new HighResolutionTimer(entry);
    }

    /// <inheritdoc/>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ThrowIfInvalidTimeout(dueTime);
        ThrowIfInvalidTimeout(period);

        return _entry.Change(dueTime, period);
    }

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

    /// <summary>
    /// Throws if a due time or period is outside the range that <see cref="System.Threading.Timer"/> accepts.
    /// </summary>
    /// <param name="value">The due time or period.</param>
    /// <param name="paramName">The name of the argument that <paramref name="value"/> came from.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>, or is longer than
    /// 4294967294 milliseconds.
    /// </exception>
    private static void ThrowIfInvalidTimeout(
        TimeSpan value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        bool isNegative = value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;
        bool isTooLong = (long)value.TotalMilliseconds > MaxSupportedTimeoutMs;

        if (isNegative || isTooLong)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "The value must be Timeout.InfiniteTimeSpan or between 0 and 4294967294 milliseconds.");
        }
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
    private static readonly ContextCallback s_invokeCallback = static s => ((TimerEntry)s!).InvokeCallback();

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

    /// <summary>Gets or sets when the timer is next due, on the <see cref="TimerScheduler"/>'s clock.</summary>
    /// <remarks>
    /// Guarded by the <see cref="TimerScheduler"/> lock. Only change it while the entry is not scheduled, because the
    /// scheduler keeps its entries sorted by this value.
    /// </remarks>
    internal TimeSpan DueTime { get; set; }

    /// <summary>
    /// Gets or sets the interval between callbacks, or <see cref="TimeSpan.Zero"/> for a one-shot timer.
    /// </summary>
    /// <remarks>Guarded by the <see cref="TimerScheduler"/> lock.</remarks>
    internal TimeSpan Period { get; set; }

    /// <summary>Reschedules the timer, unless it has been closed.</summary>
    /// <param name="dueTime">
    /// The delay before the first callback, or <see cref="Timeout.InfiniteTimeSpan"/> to stop the timer.
    /// </param>
    /// <param name="period">
    /// The interval between callbacks, or <see cref="Timeout.InfiniteTimeSpan"/> or zero for a one-shot timer.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the timer was rescheduled; <see langword="false"/> if it has been closed.
    /// </returns>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            TimerScheduler.Instance.Schedule(this, dueTime, period);
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
        if (!TryStartCallback())
        {
            return;
        }

        try
        {
            RunCallback();
        }
        finally
        {
            EndCallback();
        }
    }

    /// <summary>Records that a callback is starting, unless the timer has been closed.</summary>
    /// <returns>
    /// <see langword="true"/> if the callback should run; <see langword="false"/> if it should be skipped.
    /// </returns>
    private bool TryStartCallback()
    {
        lock (_lock)
        {
            // A callback queued just before Close() is skipped rather than run after disposal.
            if (_closed)
            {
                return false;
            }

            _callbacksRunning++;
            return true;
        }
    }

    /// <summary>Invokes the callback in the captured execution context, if there is one.</summary>
    private void RunCallback()
    {
        if (_executionContext is null)
        {
            InvokeCallback();
        }
        else
        {
            ExecutionContext.Run(_executionContext, s_invokeCallback, this);
        }
    }

    /// <summary>
    /// Records that a callback has finished, and completes <see cref="CloseAsync"/> if it was the last one running
    /// after the timer was closed.
    /// </summary>
    private void EndCallback()
    {
        lock (_lock)
        {
            _callbacksRunning--;

            if (_callbacksRunning == 0 && _closed)
            {
                _closeCompletion?.TrySetResult();
            }
        }
    }

    /// <summary>Invokes the callback with its state.</summary>
    private void InvokeCallback() => _callback(_state);
}
