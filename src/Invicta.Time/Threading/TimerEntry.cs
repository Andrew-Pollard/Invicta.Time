// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.Versioning;

namespace Invicta.Threading;

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
    /// Guarded by the <see cref="TimerScheduler"/> lock. Only the scheduler changes it, and it removes the entry from
    /// its sorted set first, because the set is ordered by this value.
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
