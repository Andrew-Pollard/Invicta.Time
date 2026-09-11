// © 2026 Andrew Pollard. All rights reserved.

using System.Runtime.Versioning;

namespace Invicta;

/// <summary>
/// Extension members that expose <see cref="HighResolutionTimeProvider"/> on <see cref="TimeProvider"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17134")]
public static class TimeProviderExtensions
{
    private static readonly HighResolutionTimeProvider s_highResolution = new();

    extension(TimeProvider)
    {
        /// <summary>
        /// Gets a <see cref="TimeProvider"/> whose timers have sub-millisecond resolution, as a drop-in
        /// replacement for <see cref="TimeProvider.System"/>.
        /// </summary>
        /// <remarks>
        /// The returned instance is a <see cref="HighResolutionTimeProvider"/>. Requires Windows 10 version 1803
        /// or later; check <see cref="HighResolutionTimeProvider.IsSupported"/> before use.
        /// </remarks>
        public static TimeProvider HighResolution => s_highResolution;
    }
}
