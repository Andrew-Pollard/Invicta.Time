// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Invicta.Threading;

/// <summary>
/// Owns one high-resolution waitable timer and one background thread. Pending timers live in a sorted set ordered
/// by due time; the kernel timer is always armed for the earliest one.
/// </summary>
/// <remarks>
/// There is no separate wake-up event. When a newly scheduled timer is due before the currently armed time, the
/// calling thread re-arms the kernel timer itself, which wakes the scheduler thread at the new time. Re-arming
/// only ever moves the deadline earlier, so the signal that <c>SetWaitableTimer</c> resets can never be one that
/// was needed.
/// </remarks>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed class TimerScheduler
{
    /// <summary>Gets the scheduler, creating it on first use.</summary>
    public static TimerScheduler Instance => s_instance.Value;
    private static readonly Lazy<TimerScheduler> s_instance = new(() => new TimerScheduler());

    private readonly Lock _lock = new();
    private readonly SortedSet<TimerEntry> _scheduled = new(Comparer<TimerEntry>.Create(CompareDueTimes));

    // Due times are measured from this Stopwatch timestamp.
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    // The kernel timer, and the due time it is armed for (TimeSpan.MaxValue if unarmed, guarded by _lock).
    private readonly SafeWaitHandle _timerHandle;
    private TimeSpan _armedDueTime = TimeSpan.MaxValue;

    /// <summary>Creates the kernel timer and starts the scheduler thread.</summary>
    /// <exception cref="Win32Exception">The kernel timer could not be created.</exception>
    private TimerScheduler()
    {
        _timerHandle = CreateKernelTimer();

        new Thread(Run)
        {
            IsBackground = true,
            Name = "High-resolution timer",
            Priority = ThreadPriority.Highest,
        }.Start();
    }

    /// <summary>Removes an entry from the schedule, then adds it back with a new due time and period.</summary>
    /// <param name="entry">The entry to schedule.</param>
    /// <param name="dueTime">
    /// The delay before the first callback, or <see cref="Timeout.InfiniteTimeSpan"/> to leave the timer stopped.
    /// </param>
    /// <param name="period">
    /// The interval between callbacks, or <see cref="Timeout.InfiniteTimeSpan"/> or zero for a one-shot timer.
    /// </param>
    public void Schedule(TimerEntry entry, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            _scheduled.Remove(entry);

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            entry.DueTime = GetCurrentTime() + dueTime;
            entry.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;

            _scheduled.Add(entry);

            if (entry.DueTime < _armedDueTime)
            {
                Arm(entry.DueTime);
            }
        }
    }

    /// <summary>Removes an entry from the schedule, so that it no longer fires.</summary>
    /// <param name="entry">The entry to remove.</param>
    public void Unschedule(TimerEntry entry)
    {
        // The kernel timer is left armed; a spurious wake-up just finds nothing due and re-arms.
        lock (_lock)
        {
            _scheduled.Remove(entry);
        }
    }

    /// <summary>
    /// Orders entries by due time, and entries due at the same time by <see cref="TimerEntry.Sequence"/>, so that
    /// no two entries compare as equal.
    /// </summary>
    /// <param name="x">The first entry.</param>
    /// <param name="y">The second entry.</param>
    /// <returns>
    /// A negative number if <paramref name="x"/> is due first, or a positive number if it is due later.
    /// </returns>
    internal static int CompareDueTimes(TimerEntry x, TimerEntry y)
    {
        int byDueTime = x.DueTime.CompareTo(y.DueTime);

        return byDueTime != 0 ? byDueTime : x.Sequence.CompareTo(y.Sequence);
    }

    /// <summary>Creates the high-resolution kernel timer that every entry shares.</summary>
    /// <returns>A handle to the timer.</returns>
    /// <exception cref="Win32Exception">The kernel timer could not be created.</exception>
    private static SafeWaitHandle CreateKernelTimer()
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

        return handle;
    }

    /// <summary>Gets the time elapsed since the scheduler started, which due times are measured against.</summary>
    /// <returns>The current time on the scheduler's clock.</returns>
    private TimeSpan GetCurrentTime() => Stopwatch.GetElapsedTime(_startTimestamp);

    /// <summary>Arms the kernel timer to signal at a due time.</summary>
    /// <param name="dueTime">The time to signal at, on the scheduler's clock.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    /// <exception cref="Win32Exception">The kernel timer could not be set.</exception>
    private void Arm(TimeSpan dueTime)
    {
        // A negative due time is relative, in 100 ns intervals, which are TimeSpan ticks. If the kernel wakes the
        // scheduler before the due time, nothing is due yet and it simply re-arms for the remainder.
        TimeSpan delay = dueTime - GetCurrentTime();
        long relativeDueTime = -Math.Max(delay.Ticks, 1);

        if (Kernel32.SetWaitableTimer(_timerHandle, in relativeDueTime, 0, nint.Zero, nint.Zero, fResume: 0) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed.");
        }

        _armedDueTime = dueTime;
    }

    /// <summary>
    /// The scheduler thread's loop: waits for the kernel timer, queues the callbacks of every entry that is due, and
    /// re-arms the kernel timer for the next entry.
    /// </summary>
    /// <exception cref="Win32Exception">The wait for the kernel timer failed.</exception>
    private void Run()
    {
        while (true)
        {
            WaitForKernelTimer();

            lock (_lock)
            {
                _armedDueTime = TimeSpan.MaxValue;

                QueueDueCallbacks();
                ArmForEarliestEntry();
            }
        }
    }

    /// <summary>Blocks until the kernel timer signals.</summary>
    /// <exception cref="Win32Exception">The wait failed.</exception>
    private void WaitForKernelTimer()
    {
        if (Kernel32.WaitForSingleObject(_timerHandle, Kernel32.INFINITE) == Kernel32.WAIT_FAILED)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject failed.");
        }
    }

    /// <summary>
    /// Queues the callback of every entry that is due to the thread pool, and reschedules the periodic ones.
    /// </summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void QueueDueCallbacks()
    {
        TimeSpan now = GetCurrentTime();
        while (_scheduled.Min is { } entry && entry.DueTime <= now)
        {
            _scheduled.Remove(entry);

            if (entry.Period > TimeSpan.Zero)
            {
                ScheduleNextTick(entry, now);
            }

            ThreadPool.UnsafeQueueUserWorkItem(entry, preferLocal: false);
        }
    }

    /// <summary>Schedules a periodic entry's next tick after the one that has just come due.</summary>
    /// <param name="entry">The periodic entry, which must not currently be scheduled.</param>
    /// <param name="now">The current time on the scheduler's clock.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void ScheduleNextTick(TimerEntry entry, TimeSpan now)
    {
        // Keep a drift-free cadence, but if we've fallen more than a period behind, skip the missed ticks rather
        // than firing a burst of catch-up callbacks.
        TimeSpan nextDueTime = entry.DueTime + entry.Period;
        entry.DueTime = nextDueTime > now ? nextDueTime : now + entry.Period;

        _scheduled.Add(entry);
    }

    /// <summary>Arms the kernel timer for the entry that is due soonest, if there is one.</summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void ArmForEarliestEntry()
    {
        if (_scheduled.Min is { } earliest)
        {
            Arm(earliest.DueTime);
        }
    }
}
