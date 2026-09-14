// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace Invicta.Threading;

/// <summary>
/// Represents a scheduled timer, and is the <see cref="ITimer"/> handed to callers. Scheduling state is guarded by
/// the <see cref="TimerScheduler"/> lock; lifetime state by the entry's own lock.
/// </summary>
/// <param name="callback">The method to invoke each time the timer fires.</param>
/// <param name="state">The object to pass to <paramref name="callback"/>.</param>
/// <param name="executionContext">
/// The context to invoke <paramref name="callback"/> in, or <see langword="null"/> to invoke it in whatever context
/// the thread pool thread has.
/// </param>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class TimerEntry(TimerCallback callback, object? state, ExecutionContext? executionContext)
    : ITimer, IThreadPoolWorkItem
{
    // Matches System.Threading.Timer's upper bound (0xFFFFFFFE ms, ~49.7 days).
    private const long MaxSupportedTimeoutMs = 0xFFFFFFFE;

    private static readonly ContextCallback s_invokeCallback = static s => ((TimerEntry)s!).InvokeCallback();

    private static long s_lastId;

    private readonly TimerCallback _callback = callback;
    private readonly object? _state = state;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The entry doesn't own the captured context, and ExecutionContext.Dispose does nothing.")]
    private readonly ExecutionContext? _executionContext = executionContext;

    private readonly Lock _lock = new();

    // Guarded by _lock.
    private bool _closed;
    private int _callbacksRunning;
    private TaskCompletionSource? _closeCompletion;

    /// <summary>Gets a number that uniquely identifies this entry.</summary>
    /// <remarks>
    /// The scheduler uses it to distinguish entries that are due at the same time, so that its sorted set does not
    /// treat them as duplicates.
    /// </remarks>
    internal long Id { get; } = Interlocked.Increment(ref s_lastId);

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

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dueTime"/> or <paramref name="period"/> is negative and not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or is longer than 4294967294 milliseconds.
    /// </exception>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ThrowIfInvalidTimeout(dueTime);
        ThrowIfInvalidTimeout(period);

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
    public void Dispose()
    {
        lock (_lock)
        {
            DisposeCore();
        }
    }

    /// <summary>Stops the timer, as <see cref="Dispose"/> does.</summary>
    /// <returns>A task that completes once any callbacks already running have finished.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            DisposeCore();

            if (_callbacksRunning == 0)
            {
                return default;
            }

            _closeCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_closeCompletion.Task);
        }
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
                $"The value must be Timeout.InfiniteTimeSpan or between 0 and {MaxSupportedTimeoutMs} milliseconds.");
        }
    }

    /// <summary>Marks the timer closed and removes it from the scheduler, if it is not already closed.</summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void DisposeCore()
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
            // A callback queued just before Dispose() is skipped rather than run after disposal.
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
    /// Records that a callback has finished, and completes <see cref="DisposeAsync"/> if it was the last one running
    /// after the timer was disposed.
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
