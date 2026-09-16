// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

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
    private readonly ExecutionContext? _executionContext;

    private readonly IWorkItemRegistration _registration;

    // Guarded by _lock.
    private bool _disposed;
    private int _callbacksRunning;
    private TaskCompletionSource? _disposalCompletion;
    private readonly Lock _lock = new();

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
        ThrowIfInvalidDueTimeOrPeriod(dueTime);
        ThrowIfInvalidDueTimeOrPeriod(period);

        return _registration.Change(dueTime, period);
    }

    private static void ThrowIfInvalidDueTimeOrPeriod(
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

    void IThreadPoolWorkItem.Execute()
    {
        if (!TryStartCallback())
        {
            return;
        }

        try
        {
            InvokeCallbackInCapturedContext();
        }
        finally
        {
            EndCallback();
        }
    }

    private bool TryStartCallback()
    {
        lock (_lock)
        {
            // A callback queued just before Dispose() is skipped rather than run after disposal.
            if (_disposed)
            {
                return false;
            }

            _callbacksRunning++;
            return true;
        }
    }

    private void InvokeCallbackInCapturedContext()
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

    private void EndCallback()
    {
        lock (_lock)
        {
            _callbacksRunning--;

            if (_callbacksRunning == 0 && _disposed)
            {
                _disposalCompletion?.TrySetResult();
            }
        }
    }

    private void InvokeCallback()
    {
        _callback(_state);
    }

    /// <summary>
    /// Stops the timer. Callbacks that are queued but have not started are skipped; callbacks already running are
    /// not waited for.
    /// </summary>
    public void Dispose()
    {
        // Cancelling stops the scheduler queueing callbacks; closing skips any it has queued already.
        _registration.Cancel();

        lock (_lock)
        {
            _disposed = true;
        }
    }

    /// <summary>Stops the timer, as <see cref="Dispose"/> does.</summary>
    /// <returns>A task that completes once any callbacks already running have finished.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();

        lock (_lock)
        {
            if (_callbacksRunning == 0)
            {
                return default;
            }

            _disposalCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(_disposalCompletion.Task);
        }
    }
}
