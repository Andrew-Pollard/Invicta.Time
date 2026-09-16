// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Invicta.Threading;

/// <summary>
/// A Windows high-resolution waitable timer. A thread blocks on it with <see cref="WaitHandle.WaitOne()"/>, and
/// <see cref="Arm"/> sets when it next signals.
/// </summary>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class KernelTimer : WaitHandle
{
    /// <summary>Creates the timer, with the rights to wait on it and to set it.</summary>
    /// <exception cref="Win32Exception">The timer could not be created.</exception>
    public KernelTimer()
    {
        SafeWaitHandle handle = Kernel32.CreateWaitableTimerExW(
            lpTimerAttributes: nint.Zero,
            lpTimerName: null,
            Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            Kernel32.TIMER_MODIFY_STATE | Kernel32.SYNCHRONIZE);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWaitableTimerExW failed.");
        }

        SafeWaitHandle = handle;
    }

    /// <summary>Sets the timer to signal after a delay, replacing whatever it was set to before.</summary>
    /// <param name="delay">
    /// The time until the timer signals; anything below one tick signals as soon as possible.
    /// </param>
    /// <exception cref="Win32Exception">The timer could not be set.</exception>
    public void Arm(TimeSpan delay)
    {
        // A negative due time is relative, in 100 ns intervals, which are TimeSpan ticks.
        long relativeDueTime = -Math.Max(delay.Ticks, 1);

        bool armed = Kernel32.SetWaitableTimer(
            SafeWaitHandle, in relativeDueTime, 0, nint.Zero, nint.Zero, fResume: false);

        if (!armed)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed.");
        }
    }
}
