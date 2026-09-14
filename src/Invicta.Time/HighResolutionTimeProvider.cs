// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.Versioning;

using Invicta.Threading;

namespace Invicta;

/// <summary>
/// Provides <see cref="TimeProvider"/> timers driven by a Windows high-resolution waitable timer
/// (<c>CreateWaitableTimerExW</c> with <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>), giving sub-millisecond
/// timer resolution instead of the ~15.6 ms system clock tick used by <see cref="TimeProvider.System"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only timer creation differs from <see cref="TimeProvider.System"/>. <see cref="TimeProvider.GetUtcNow"/>
/// and <see cref="TimeProvider.GetTimestamp"/> are already high resolution
/// (<c>GetSystemTimePreciseAsFileTime</c> and <c>QueryPerformanceCounter</c>), so they're inherited unchanged.
/// </para>
/// <para>
/// All timers share one dedicated scheduler thread and one kernel timer object.
/// </para>
/// <para>
/// Requires Windows 10 version 1803 (build 17134) or later. Check <see cref="IsSupported"/> before using
/// <see cref="Instance"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17134")]
public sealed class HighResolutionTimeProvider : TimeProvider
{
    private HighResolutionTimeProvider() { }

    /// <summary>Gets the shared <see cref="HighResolutionTimeProvider"/> instance.</summary>
    public static HighResolutionTimeProvider Instance { get; } = new();

    /// <summary>Gets a value indicating whether the current OS supports high-resolution waitable timers.</summary>
    [SupportedOSPlatformGuard("windows10.0.17134")]
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "High-resolution waitable timers require Windows 10 version 1803 or later.");
        }

        TimerEntry timer = new(callback, state, ExecutionContext.Capture());
        timer.Change(dueTime, period);

        return timer;
    }
}
