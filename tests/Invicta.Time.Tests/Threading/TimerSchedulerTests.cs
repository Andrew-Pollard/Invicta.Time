// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

namespace Invicta.Threading;

internal sealed class TimerSchedulerTests
{
    [Test]
    public void CompareDueTimes_DifferentDueTimes_OrdersByDueTime()
    {
        TimerEntry later = NewEntry(dueTimestamp: 200);
        TimerEntry earlier = NewEntry(dueTimestamp: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(earlier, later), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(later, earlier), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameDueTime_OrdersByCreation()
    {
        TimerEntry first = NewEntry(dueTimestamp: 100);
        TimerEntry second = NewEntry(dueTimestamp: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(first, second), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(second, first), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameEntry_IsZero()
    {
        TimerEntry entry = NewEntry(dueTimestamp: 100);

        Assert.That(TimerScheduler.CompareDueTimes(entry, entry), Is.Zero);
    }

    [Test]
    public void SortedSet_EntriesWithSameDueTime_AreAllKeptInCreationOrder()
    {
        TimerEntry[] entries = [.. Enumerable.Range(0, 5).Select(_ => NewEntry(dueTimestamp: 100))];
        SortedSet<TimerEntry> scheduled = new(Comparer<TimerEntry>.Create(TimerScheduler.CompareDueTimes));

        foreach (TimerEntry entry in entries.Reverse())
        {
            scheduled.Add(entry);
        }

        Assert.That(scheduled, Is.EqualTo(entries));
    }

    private static TimerEntry NewEntry(long dueTimestamp) =>
        new(static _ => { }, null, null) { DueTimestamp = dueTimestamp };
}
