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
    public void CompareDueTimes_SameDueTime_IsNotEqual()
    {
        TimerEntry first = NewEntry(s_earlier);
        TimerEntry second = NewEntry(s_earlier);

        int firstToSecond = TimerScheduler.CompareDueTimes(first, second);
        int secondToFirst = TimerScheduler.CompareDueTimes(second, first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstToSecond, Is.Not.Zero);
            Assert.That(Math.Sign(secondToFirst), Is.EqualTo(-Math.Sign(firstToSecond)));
        }
    }

    [Test]
    public void CompareDueTimes_SameEntry_IsZero()
    {
        TimerEntry entry = NewEntry(s_earlier);

        Assert.That(TimerScheduler.CompareDueTimes(entry, entry), Is.Zero);
    }

    [Test]
    public void CompareDueTimes_InSortedSetWithSameDueTimes_KeepsAllEntries()
    {
        TimerEntry[] entries = [.. Enumerable.Range(0, 5).Select(_ => NewEntry(s_earlier))];
        SortedSet<TimerEntry> scheduled = new(Comparer<TimerEntry>.Create(TimerScheduler.CompareDueTimes));

        foreach (TimerEntry entry in entries)
        {
            scheduled.Add(entry);
        }

        Assert.That(scheduled, Is.EquivalentTo(entries));
    }

    [Test]
    public void GetNextDueTime_TickOnTime_KeepsCadence()
    {
        TimeSpan nextDueTime = TimerScheduler.GetNextDueTime(
            dueTime: s_earlier, period: TimeSpan.FromMilliseconds(10), now: s_earlier);

        Assert.That(nextDueTime, Is.EqualTo(TimeSpan.FromMilliseconds(110)));
    }

    [Test]
    public void GetNextDueTime_TickLessThanAPeriodLate_KeepsCadence()
    {
        TimeSpan nextDueTime = TimerScheduler.GetNextDueTime(
            dueTime: s_earlier, period: TimeSpan.FromMilliseconds(10), now: TimeSpan.FromMilliseconds(109));

        Assert.That(nextDueTime, Is.EqualTo(TimeSpan.FromMilliseconds(110)));
    }

    [Test]
    public void GetNextDueTime_TickAWholePeriodLate_SkipsMissedTickAndRestartsFromNow()
    {
        TimeSpan nextDueTime = TimerScheduler.GetNextDueTime(
            dueTime: s_earlier, period: TimeSpan.FromMilliseconds(10), now: TimeSpan.FromMilliseconds(110));

        Assert.That(nextDueTime, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
    }

    [Test]
    public void GetNextDueTime_TickSeveralPeriodsLate_SkipsMissedTicksAndRestartsFromNow()
    {
        TimeSpan nextDueTime = TimerScheduler.GetNextDueTime(
            dueTime: s_earlier, period: TimeSpan.FromMilliseconds(10), now: TimeSpan.FromMilliseconds(137));

        Assert.That(nextDueTime, Is.EqualTo(TimeSpan.FromMilliseconds(147)));
    }

    private static TimerEntry NewEntry(TimeSpan dueTime) =>
        new(static _ => { }, null, null) { DueTime = dueTime };
}
