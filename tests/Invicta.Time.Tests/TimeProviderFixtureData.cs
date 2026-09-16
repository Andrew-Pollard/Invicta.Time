// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

namespace Invicta;

/// <summary>
/// Supplies the providers that shared-behavior fixtures run against, and the timings their tests use. Running each
/// test against <see cref="TimeProvider.System"/> as well keeps the two providers from drifting apart.
/// </summary>
internal static class TimeProviderFixtureData
{
    /// <summary>
    /// Gets a due time comfortably longer than the ~15.6 ms tick that <see cref="TimeProvider.System"/>'s timers are
    /// limited to.
    /// </summary>
    public static TimeSpan DueTime { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets a period comfortably longer than the system clock tick.</summary>
    public static TimeSpan Period { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets how long a test waits for a callback before failing.</summary>
    public static TimeSpan CallbackTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the allowance for <see cref="TimeProvider.System"/> rounding to its tick, which can deliver a callback
    /// just before <see cref="System.Diagnostics.Stopwatch"/> agrees the due time has elapsed.
    /// </summary>
    public static TimeSpan TickTolerance { get; } = TimeSpan.FromMilliseconds(16);

    /// <summary>Creates fixture data for both providers.</summary>
    /// <returns>Fixture data for <see cref="TimeProvider.System"/> and the high-resolution provider.</returns>
    public static IEnumerable<TestFixtureData> Providers()
    {
        yield return new TestFixtureData(TimeProvider.System).SetArgDisplayNames("System");
        yield return new TestFixtureData(HighResolutionTimeProvider.Instance).SetArgDisplayNames("HighResolution");
    }
}
