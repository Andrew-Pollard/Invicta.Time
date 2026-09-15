// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace Invicta.Threading;

/// <summary>Represents a scheduled timer, and is the <see cref="ITimer"/> handed to callers.</summary>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class HighResolutionTimer : ITimer, IThreadPoolWorkItem
{
    // Matches System.Threading.Timer's upper bound (0xFFFFFFFE ms, ~49.7 days).
    private const long MaxSupportedTimeoutMs = 0xFFFFFFFE;

    private static readonly ContextCallback s_invokeCallback = static s => ((HighResolutionTimer)s!).InvokeCallback();

    private readonly TimerCallback _callback;
    private readonly object? _state;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The timer doesn't own the captured context, and ExecutionContext.Dispose does nothing.")]
    private readonly ExecutionContext? _executionContext;

    private readonly IWorkItemRegistration _registration;

    private readonly Lock _lock = new();

    // Guarded by _lock.
    private bool _closed;
    private int _callbacksRunning;
    private TaskCompletionSource? _closeCompletion;

    /// <summary>Creates a timer that is not yet scheduled.</summary>
    /// <param name="callback">The method to invoke each time the timer fires.</param>
    /// <param name="state">The object to pass to <paramref name="callback"/>.</param>
    /// <param name="executionContext">
    /// The context to invoke <paramref name="callback"/> in, or <see langword="null"/> to invoke it in whatever
    /// context the thread pool thread has.
    /// </param>
    public HighResolutionTimer(TimerCallback callback, object? state, ExecutionContext? executionContext)
    {
        _callback = callback;
        _state = state;
        _executionContext = executionContext;
        _registration = TimerScheduler.Register(this);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dueTime"/> or <paramref name="period"/> is negative and not
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or is longer than 4294967294 milliseconds.
    /// </exception>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ThrowIfInvalidTimeout(dueTime);
        ThrowIfInvalidTimeout(period);

        return _registration.Change(dueTime, period);
    }

    /// <summary>
    /// Stops the timer. Callbacks that are queued but have not started are skipped; callbacks already running are
    /// not waited for.
    /// </summary>
    public void Dispose()
    {
        _registration.Cancel();

        lock (_lock)
        {
            _closed = true;
        }
    }

    /// <summary>Stops the timer, as <see cref="Dispose"/> does.</summary>
    /// <returns>A task that completes once any callbacks already running have finished.</returns>
    public ValueTask DisposeAsync()
    {
        _registration.Cancel();

        lock (_lock)
        {
            _closed = true;

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
