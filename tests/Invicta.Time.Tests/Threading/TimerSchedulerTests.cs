// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

namespace Invicta.Threading;

internal sealed class TimerSchedulerTests
{
    private static readonly TimeSpan s_earlier = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_later = TimeSpan.FromMilliseconds(200);

    [Test]
    public void CompareDueTimes_DifferentDueTimes_OrdersByDueTime()
    {
        TimerEntry later = NewEntry(s_later);
        TimerEntry earlier = NewEntry(s_earlier);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(earlier, later), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(later, earlier), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameDueTime_OrdersByCreation()
    {
        TimerEntry first = NewEntry(s_earlier);
        TimerEntry second = NewEntry(s_earlier);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(first, second), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(second, first), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameEntry_IsZero()
    {
        TimerEntry entry = NewEntry(s_earlier);

        Assert.That(TimerScheduler.CompareDueTimes(entry, entry), Is.Zero);
    }

    [Test]
    public void SortedSet_EntriesWithSameDueTime_AreAllKeptInCreationOrder()
    {
        TimerEntry[] entries = [.. Enumerable.Range(0, 5).Select(_ => NewEntry(s_earlier))];
        SortedSet<TimerEntry> scheduled = new(Comparer<TimerEntry>.Create(TimerScheduler.CompareDueTimes));

        foreach (TimerEntry entry in entries.Reverse())
        {
            scheduled.Add(entry);
        }

        Assert.That(scheduled, Is.EqualTo(entries));
    }

    private static TimerEntry NewEntry(TimeSpan dueTime) =>
        new(static _ => { }, null, null) { DueTime = dueTime };
}
