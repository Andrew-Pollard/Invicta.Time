// © 2026 Andrew Pollard. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Invicta.Threading;

/// <summary>
/// Owns one high-resolution waitable timer and one background thread. Pending timers live in a min-heap keyed
/// on their due <see cref="Stopwatch"/> timestamp; the kernel timer is always armed for the earliest one.
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
    private readonly TimerHeap _heap = new();

    // The kernel timer, and the Stopwatch timestamp it is armed for (long.MaxValue if unarmed, guarded by _lock).
    private readonly SafeWaitHandle _timerHandle;
    private long _armedDue = long.MaxValue;

    /// <summary>Creates the kernel timer and starts the scheduler thread.</summary>
    /// <exception cref="Win32Exception">The kernel timer could not be created.</exception>
    private TimerScheduler()
    {
        _timerHandle = Kernel32.CreateWaitableTimerExW(
            lpTimerAttributes: nint.Zero,
            lpTimerName: null,
            Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            Kernel32.TIMER_MODIFY_STATE | Kernel32.SYNCHRONIZE);

        if (_timerHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWaitableTimerExW failed.");
        }

        new Thread(Run)
        {
            IsBackground = true,
            Name = "High-resolution timer",
            Priority = ThreadPriority.Highest,
        }.Start();
    }

    /// <summary>Removes an entry from the schedule, then adds it back with a new due time and period.</summary>
    /// <param name="entry">The entry to schedule.</param>
    /// <param name="dueTicks"><see cref="Stopwatch"/> ticks from now, or -1 to leave the timer stopped.</param>
    /// <param name="periodTicks"><see cref="Stopwatch"/> ticks between callbacks; 0 or -1 for a one-shot timer.</param>
    public void Schedule(TimerEntry entry, long dueTicks, long periodTicks)
    {
        lock (_lock)
        {
            _heap.Remove(entry);

            if (dueTicks < 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            entry.DueTimestamp = now + dueTicks;
            entry.PeriodTicks = periodTicks > 0 ? periodTicks : 0;

            _heap.Insert(entry);

            if (entry.DueTimestamp < _armedDue)
            {
                Arm(entry.DueTimestamp, now);
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
            _heap.Remove(entry);
        }
    }

    /// <summary>Arms the kernel timer to signal at a due time.</summary>
    /// <param name="dueTimestamp">The <see cref="Stopwatch"/> timestamp to signal at.</param>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    /// <exception cref="Win32Exception">The kernel timer could not be set.</exception>
    private void Arm(long dueTimestamp, long now)
    {
        // Relative due times are negative, in 100 ns units. Round up so we never wake before the due time
        // (if the kernel still wakes us slightly early, the loop just re-arms for the remainder).
        long delta = Math.Max(dueTimestamp - now, 0);
        long hundredNs = Stopwatch.Frequency == TimeSpan.TicksPerSecond
            ? delta
            : (long)(((Int128)delta * TimeSpan.TicksPerSecond + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
        long relative = -Math.Max(hundredNs, 1);

        if (Kernel32.SetWaitableTimer(_timerHandle, in relative, 0, nint.Zero, nint.Zero, fResume: 0) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed.");
        }

        _armedDue = dueTimestamp;
    }

    /// <summary>
    /// The scheduler thread's loop: waits for the kernel timer, queues the callbacks of every entry that is due,
    /// reschedules the periodic ones, and re-arms the kernel timer for the next entry.
    /// </summary>
    /// <exception cref="Win32Exception">The wait for the kernel timer failed.</exception>
    private void Run()
    {
        while (true)
        {
            if (Kernel32.WaitForSingleObject(_timerHandle, Kernel32.INFINITE) == Kernel32.WAIT_FAILED)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject failed.");
            }

            lock (_lock)
            {
                _armedDue = long.MaxValue;

                long now = Stopwatch.GetTimestamp();
                while (_heap.Peek() is { } entry && entry.DueTimestamp <= now)
                {
                    _heap.RemoveMin();

                    if (entry.PeriodTicks > 0)
                    {
                        // Keep a drift-free cadence, but if we've fallen more than a period behind,
                        // skip the missed ticks rather than firing a burst of catch-up callbacks.
                        long next = entry.DueTimestamp + entry.PeriodTicks;
                        entry.DueTimestamp = next > now ? next : now + entry.PeriodTicks;
                        _heap.Insert(entry);
                    }

                    ThreadPool.UnsafeQueueUserWorkItem(entry, preferLocal: false);
                }

                if (_heap.Peek() is { } earliest)
                {
                    Arm(earliest.DueTimestamp, Stopwatch.GetTimestamp());
                }
            }
        }
    }
}
