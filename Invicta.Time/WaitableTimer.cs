using Invicta.Time.Native;

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Invicta.Time;

[SupportedOSPlatform("windows10.0.17134.0")]
public sealed class WaitableTimer : WaitHandle
{
    private const uint AccessRights = Kernel32.TIMER_ALL_ACCESS;

    public WaitableTimer(WaitableTimerResetMode mode, WaitableTimerResolution resolution)
        : this(mode, resolution, null) { }

    public WaitableTimer(WaitableTimerResetMode mode, WaitableTimerResolution resolution, string? name)
    {
        uint flags = 0;
        if (mode == WaitableTimerResetMode.ManualReset)
        {
            flags |= Kernel32.CREATE_WAITABLE_TIMER_MANUAL_RESET;
        }
        if (resolution == WaitableTimerResolution.High)
        {
            flags |= Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION;
        }

        SafeWaitHandle = Kernel32.CreateWaitableTimerExW(nint.Zero, name, flags, AccessRights);

        if (SafeWaitHandle.IsInvalid)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }

    public bool Set(TimeSpan dueTime, TimeSpan period)
    {
        return Kernel32.SetWaitableTimerEx(SafeWaitHandle, -dueTime.Ticks, (int)period.TotalMilliseconds, nint.Zero, nint.Zero, nint.Zero, 0);
    }

    public bool Cancel()
    {
        return Kernel32.CancelWaitableTimer(SafeWaitHandle);
    }
}
