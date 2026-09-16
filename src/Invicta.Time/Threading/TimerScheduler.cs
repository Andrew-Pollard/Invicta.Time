// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Invicta.Threading;

/// <summary>
/// Owns one high-resolution waitable timer and one background thread, and queues work items to the thread pool when
/// they are due. Pending registrations live in a sorted set ordered by due time; the waitable timer is always armed for
/// the earliest one.
/// </summary>
/// <remarks>
/// There is no separate wake-up event. When a registration is scheduled before the currently armed time, the calling
/// thread re-arms the waitable timer itself, which wakes the scheduler thread at the new time. Re-arming only ever moves
/// the deadline earlier, so the signal it resets can never be one that was needed.
/// </remarks>
[SupportedOSPlatform("windows10.0.17134")]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The scheduler is a process-lifetime singleton, so its waitable timer is released at exit.")]
internal sealed class TimerScheduler
{
    private static readonly Lazy<TimerScheduler> s_instance = new(() => new TimerScheduler());

    private static readonly Comparer<Registration> s_dueTimeComparer =
        Comparer<Registration>.Create(static (x, y) => CompareDueTimes((x.DueTime, x.Id), (y.DueTime, y.Id)));

    // Due times are measured from this Stopwatch timestamp.
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private readonly WaitableTimer _waitableTimer = new();

    // Guarded by _lock. _armedDueTime is the due time the waitable timer is armed for, or TimeSpan.MaxValue if unarmed.
    private TimeSpan _armedDueTime = TimeSpan.MaxValue;
    private readonly SortedSet<Registration> _scheduled = new(s_dueTimeComparer);
    private readonly Lock _lock = new();

    /// <summary>Starts the scheduler thread.</summary>
    private TimerScheduler()
    {
        // At normal priority a loaded machine starves the thread: 1 ms ticks drop from 5000 to under 1000 in five
        // seconds, and the median one-shot goes from 1.9 ms to 5.8 ms.
        new Thread(Run)
        {
            IsBackground = true,
            Name = "High-resolution timer",
            Priority = ThreadPriority.Highest,
        }.Start();
    }

    /// <summary>Gets the scheduler, creating it on first use.</summary>
    /// <remarks>
    /// If the waitable timer cannot be created, every use of this property rethrows that first exception, as
    /// <see cref="Lazy{T}"/> does. Creation only fails if the system is out of resources.
    /// </remarks>
    /// <exception cref="Win32Exception">The waitable timer could not be created.</exception>
    public static TimerScheduler Instance => s_instance.Value;

    /// <summary>
    /// Orders registrations by due time, breaking ties with their IDs so that different registrations never compare
    /// as equal. <see cref="SortedSet{T}"/> treats registrations that compare as equal as duplicates, which would drop
    /// one that is due at the same time as another.
    /// </summary>
    /// <param name="x">The first registration's due time and ID.</param>
    /// <param name="y">The second registration's due time and ID.</param>
    /// <returns>
    /// A negative number if <paramref name="x"/> is due first, or a positive number if it is due later.
    /// </returns>
    internal static int CompareDueTimes((TimeSpan DueTime, long Id) x, (TimeSpan DueTime, long Id) y)
    {
        int byDueTime = x.DueTime.CompareTo(y.DueTime);

        return byDueTime != 0 ? byDueTime : x.Id.CompareTo(y.Id);
    }

    /// <summary>
    /// The scheduler thread's loop: waits for the waitable timer, queues every work item that is due, and re-arms the
    /// waitable timer for the next one.
    /// </summary>
    /// <remarks>
    /// An exception here goes unhandled on the scheduler thread and ends the process, deliberately: without the
    /// scheduler thread, no timer would ever fire again.
    /// </remarks>
    /// <exception cref="Win32Exception">The waitable timer could not be re-armed.</exception>
    private void Run()
    {
        while (true)
        {
            _waitableTimer.WaitOne();

            lock (_lock)
            {
                _armedDueTime = TimeSpan.MaxValue;

                QueueDueWorkItems();
                ArmForEarliestRegistration();
            }
        }
    }

    /// <summary>Queues every work item that is due to the thread pool, and reschedules the periodic ones.</summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void QueueDueWorkItems()
    {
        TimeSpan now = GetCurrentTime();
        while (_scheduled.Min is { } registration && registration.DueTime <= now)
        {
            if (registration.Period > TimeSpan.Zero)
            {
                AddAtDueTime(registration, GetNextDueTime(registration.DueTime, registration.Period, now));
            }
            else
            {
                _scheduled.Remove(registration);
            }

            ThreadPool.UnsafeQueueUserWorkItem(registration.WorkItem, preferLocal: false);
        }
    }

    /// <summary>
    /// Calculates when a periodic registration is next due after a tick. Ticks keep to a fixed cadence from the first
    /// due time, but if a whole period has already passed, the missed ticks are skipped rather than fired in a burst
    /// and the cadence restarts from now.
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

    /// <summary>Arms the waitable timer for whichever registration is due soonest, if there is one.</summary>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void ArmForEarliestRegistration()
    {
        if (_scheduled.Min is { } earliest)
        {
            Arm(earliest.DueTime);
        }
    }

    /// <summary>
    /// Registers a work item with the scheduler. It is not queued until the returned registration is changed.
    /// </summary>
    /// <param name="workItem">The work item to queue to the thread pool each time the registration is due.</param>
    /// <returns>The registration that controls when the work item is queued.</returns>
    public static IWorkItemRegistration Register(IThreadPoolWorkItem workItem)
    {
        return new Registration(workItem);
    }

    /// <summary>Schedules a registration with a new due time and period, replacing any schedule it has.</summary>
    /// <param name="registration">The registration to schedule.</param>
    /// <param name="dueTime">
    /// The delay before the work item is first queued, or <see cref="Timeout.InfiniteTimeSpan"/> to leave it stopped.
    /// </param>
    /// <param name="period">
    /// The interval between queuings, or <see cref="Timeout.InfiniteTimeSpan"/> or zero to queue it once.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the registration was scheduled; <see langword="false"/> if it has been cancelled.
    /// </returns>
    private bool Schedule(Registration registration, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            if (registration.IsCancelled)
            {
                return false;
            }

            registration.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                _scheduled.Remove(registration);
                return true;
            }

            TimeSpan dueAt = GetCurrentTime() + dueTime;

            if (dueTime == TimeSpan.Zero)
            {
                // Already due, so queue it here rather than waiting for the waitable timer's next step.
                AddAtDueTime(registration, dueAt);
                QueueDueWorkItems();
                ArmForEarliestRegistration();

                return true;
            }

            // Arm first, so that if arming fails the registration keeps its previous schedule.
            if (dueAt < _armedDueTime)
            {
                Arm(dueAt);
            }

            AddAtDueTime(registration, dueAt);

            return true;
        }
    }

    /// <summary>Removes a registration from the schedule for good, so that it is never queued again.</summary>
    /// <param name="registration">The registration to cancel.</param>
    private void Cancel(Registration registration)
    {
        // The waitable timer is left armed; a spurious wake-up just finds nothing due and re-arms.
        lock (_lock)
        {
            registration.IsCancelled = true;
            _scheduled.Remove(registration);
        }
    }

    /// <summary>
    /// Sets a registration's due time and adds it to the schedule. The registration is removed from the schedule
    /// first, because the sorted set is ordered by due time and would be corrupted if it changed while the
    /// registration was in it.
    /// </summary>
    /// <param name="registration">The registration to add.</param>
    /// <param name="dueTime">The time the registration is due, on the scheduler's clock.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    private void AddAtDueTime(Registration registration, TimeSpan dueTime)
    {
        _scheduled.Remove(registration);

        registration.DueTime = dueTime;
        _scheduled.Add(registration);
    }

    /// <summary>Arms the waitable timer to signal at a due time.</summary>
    /// <param name="dueTime">The time to signal at, on the scheduler's clock.</param>
    /// <remarks>The caller must hold <see cref="_lock"/>.</remarks>
    /// <exception cref="Win32Exception">The waitable timer could not be set.</exception>
    private void Arm(TimeSpan dueTime)
    {
        // If the kernel wakes the scheduler before the due time, nothing is due yet and it simply re-arms for the
        // remainder.
        _waitableTimer.Set(dueTime - GetCurrentTime());
        _armedDueTime = dueTime;
    }

    /// <summary>Gets the time elapsed since the scheduler started, which due times are measured against.</summary>
    /// <returns>The current time on the scheduler's clock.</returns>
    private TimeSpan GetCurrentTime()
    {
        // GetElapsedTime converts through a double, so where Stopwatch.Frequency is not TimeSpan.TicksPerSecond the
        // result can be a 100 ns tick out. That is negligible next to the waitable timer's steps of roughly 0.5 ms.
        return Stopwatch.GetElapsedTime(_startTimestamp);
    }

    /// <summary>Represents a work item's place in the schedule.</summary>
    /// <param name="workItem">The work item to queue to the thread pool each time the registration is due.</param>
    private sealed class Registration(IThreadPoolWorkItem workItem) : IWorkItemRegistration
    {
        private static long s_lastId;

        /// <summary>Gets the work item to queue to the thread pool each time the registration is due.</summary>
        internal IThreadPoolWorkItem WorkItem { get; } = workItem;

        /// <summary>Gets a number that uniquely identifies this registration.</summary>
        /// <remarks>
        /// The scheduler uses it to distinguish registrations that are due at the same time, so that its sorted set
        /// does not treat them as duplicates.
        /// </remarks>
        internal long Id { get; } = Interlocked.Increment(ref s_lastId);

        /// <summary>Gets or sets when the registration is next due, on the scheduler's clock.</summary>
        /// <remarks>
        /// Guarded by the scheduler's lock. Only the scheduler changes it, and it removes the registration from its
        /// sorted set first, because the set is ordered by this value.
        /// </remarks>
        internal TimeSpan DueTime { get; set; }

        /// <summary>
        /// Gets or sets the interval between queuings, or <see cref="TimeSpan.Zero"/> to queue the work item once.
        /// </summary>
        /// <remarks>Guarded by the scheduler's lock.</remarks>
        internal TimeSpan Period { get; set; }

        /// <summary>Gets or sets a value indicating whether the registration has been cancelled for good.</summary>
        /// <remarks>Guarded by the scheduler's lock.</remarks>
        internal bool IsCancelled { get; set; }

        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return Instance.Schedule(this, dueTime, period);
        }

        /// <inheritdoc/>
        public void Cancel()
        {
            Instance.Cancel(this);
        }
    }
}
