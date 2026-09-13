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
    /// <remarks>
    /// If the kernel timer cannot be created, every use of this property rethrows that first exception, as
    /// <see cref="Lazy{T}"/> does. Creation only fails if the system is out of resources.
    /// </remarks>
    /// <exception cref="Win32Exception">The kernel timer could not be created.</exception>
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

    /// <summary>Schedules an entry with a new due time and period, replacing any schedule it already has.</summary>
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
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                _scheduled.Remove(entry);
                return;
            }

            // Arm first, so that if arming fails the entry keeps its previous schedule.
            TimeSpan entryDueTime = GetCurrentTime() + dueTime;
            if (entryDueTime < _armedDueTime)
            {
                Arm(entryDueTime);
            }

            entry.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            AddAtDueTime(entry, entryDueTime);
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
    /// Orders entries by due time, breaking ties with <see cref="TimerEntry.Id"/> so that different entries never
    /// compare as equal. <see cref="SortedSet{T}"/> treats entries that compare as equal as duplicates, which would
    /// drop a timer that is due at the same time as another.
    /// </summary>
    /// <param name="x">The first entry.</param>
    /// <param name="y">The second entry.</param>
    /// <returns>
    /// A negative number if <paramref name="x"/> is due first, or a positive number if it is due later.
    /// </returns>
    internal static int CompareDueTimes(TimerEntry x, TimerEntry y)
    {
        int byDueTime = x.DueTime.CompareTo(y.DueTime);

        return byDueTime != 0 ? byDueTime : x.Id.CompareTo(y.Id);
    }

    /// <summary>
    /// Calculates when a periodic timer is next due after a tick. Ticks keep to a fixed cadence from the first due
    /// time, but if a whole period has already passed, the missed ticks are skipped rather than fired in a burst and
    /// the cadence restarts from now.
    /// </summary>
    /// <param name="dueTime">The due time of the tick that has just come due.</param>
    /// <param name="period">The interval between ticks.</param>
    /// <param name="now">The current time on the scheduler's clock.</param>
    /// <returns>The due time of the next tick.</returns>
    internal static TimeSpan GetNextDueTime(TimeSpan dueTime, TimeSpan period, TimeSpan now)
    {
        TimeSpan nextDueTime = dueTime + period;

        return nextDueTime > now ? nextDueTime : now + period;
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
    private TimeSpan GetCurrentTime()
    {
        // GetElapsedTime converts through a double, so where Stopwatch.Frequency is not TimeSpan.TicksPerSecond the
        // result can be a 100 ns tick out. That is negligible next to the kernel timer's steps of roughly 0.5 ms.
        return Stopwatch.GetElapsedTime(_startTimestamp);
    }

    /// <summary>
    /// Sets an entry's due time and adds it to the schedule. The entry is removed from the schedule first, because
    /// the sorted set is ordered by due time and would be corrupted if it changed while the entry was in it.
    /// </summary>
    /// <param name="entry">The entry to add.</param>
    /// <param name="dueTime">The time the entry is due, on the scheduler's clock.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void AddAtDueTime(TimerEntry entry, TimeSpan dueTime)
    {
        _scheduled.Remove(entry);

        entry.DueTime = dueTime;
        _scheduled.Add(entry);
    }

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
    /// <remarks>
    /// An exception here goes unhandled on the scheduler thread and ends the process, deliberately: without the
    /// scheduler thread, no timer would ever fire again.
    /// </remarks>
    /// <exception cref="Win32Exception">The kernel timer could not be waited on or re-armed.</exception>
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
            if (entry.Period > TimeSpan.Zero)
            {
                AddAtDueTime(entry, GetNextDueTime(entry.DueTime, entry.Period, now));
            }
            else
            {
                _scheduled.Remove(entry);
            }

            ThreadPool.UnsafeQueueUserWorkItem(entry, preferLocal: false);
        }
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
