using System.Runtime.Versioning;

namespace Invicta.Time;

internal enum WaitableTimerResolution
{
    Default,
    [SupportedOSPlatform("windows10.0.17134.0")]
    High
}
