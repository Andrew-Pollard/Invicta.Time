// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

namespace Invicta.Threading;

/// <summary>Controls when <see cref="TimerScheduler"/> queues a registered work item to the thread pool.</summary>
internal interface IWorkItemRegistration
{
    /// <summary>Schedules the work item with a new due time and period, replacing any schedule it has.</summary>
    /// <param name="dueTime">
    /// The delay before the work item is first queued, or <see cref="Timeout.InfiniteTimeSpan"/> to leave it stopped.
    /// </param>
    /// <param name="period">
    /// The interval between queuings, or <see cref="Timeout.InfiniteTimeSpan"/> or zero to queue it once.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the work item was scheduled; <see langword="false"/> if the registration has been
    /// cancelled.
    /// </returns>
    public bool Change(TimeSpan dueTime, TimeSpan period);

    /// <summary>Stops the work item being queued again, and makes every later <see cref="Change"/> fail.</summary>
    public void Cancel();
}
