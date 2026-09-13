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
                $"The value must be Timeout.InfiniteTimeSpan or between 0 and {MaxSupportedTimeoutMs} milliseconds.");
        }
    }
}
