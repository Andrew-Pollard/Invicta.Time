using Invicta.Time.Native;
using System.ComponentModel;
using System.Diagnostics;

using static Invicta.Time.Native.Avrt;
using static Invicta.Time.Native.Kernel32;
using static Invicta.Time.Native.NtDll;
using static Invicta.Time.Native.Winmm;

namespace Invicta.Time;

public static class HighResolutionTimer
{
    private static readonly TimeSpan s_timerResolution;
    private static readonly nint s_timer;

    static HighResolutionTimer()
    {
        uint taskIndex = 0;
        nint task = AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
        if (task == nint.Zero)
        {
            throw new Win32Exception();
        }
        _ = AvSetMmThreadPriority(task, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);

        _ = timeBeginPeriod(1);
        _ = NtQueryTimerResolution(out _, out int max, out _);
        s_timerResolution = TimeSpan.FromTicks(max);

        s_timer = CreateWaitableTimerExW(
            nint.Zero,
            nint.Zero,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS
        );
        if (s_timer == nint.Zero)
        {
            throw new Win32Exception();
        }
    }

    public static unsafe void Sleep(TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();

        // sleep
        long safeTicks = ((timeout.Ticks / s_timerResolution.Ticks) - 1) * s_timerResolution.Ticks;
        if (safeTicks > s_timerResolution.Ticks)
        {
            _ = SetWaitableTimerEx(s_timer, -safeTicks, 0, null, nint.Zero, nint.Zero, 0);
            _ = WaitForSingleObject(s_timer, INFINITE);
        }

        // spin
        SpinWait spinWait = new();
        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            spinWait.SpinOnce(-1);
        }
    }

    /// https://blog.bearcats.nl/perfect-sleep-function/
    /// https://www.siliceum.com/en/blog/post/windows-high-resolution-timers/
    /// https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/
}
