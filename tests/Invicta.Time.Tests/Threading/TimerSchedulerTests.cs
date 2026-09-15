// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

namespace Invicta.Threading;

internal sealed class TimerSchedulerTests
{
    private static readonly TimeSpan s_earlier = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_later = TimeSpan.FromMilliseconds(200);

    [Test]
    public void CompareDueTimes_DifferentDueTimes_OrdersByDueTimeBeforeId()
    {
        (TimeSpan, long) later = (s_later, 1);
        (TimeSpan, long) earlier = (s_earlier, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(earlier, later), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(later, earlier), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameDueTimeDifferentIds_IsNotEqual()
    {
        (TimeSpan, long) first = (s_earlier, 1);
        (TimeSpan, long) second = (s_earlier, 2);

        int firstToSecond = TimerScheduler.CompareDueTimes(first, second);
        int secondToFirst = TimerScheduler.CompareDueTimes(second, first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstToSecond, Is.Not.Zero);
            Assert.That(Math.Sign(secondToFirst), Is.EqualTo(-Math.Sign(firstToSecond)));
        }
    }

    [Test]
    public void CompareDueTimes_SameDueTimeAndId_IsZero()
    {
        (TimeSpan, long) registration = (s_earlier, 1);

        Assert.That(TimerScheduler.CompareDueTimes(registration, registration), Is.Zero);
    }

    [Test]
    public void CompareDueTimes_InSortedSetWithSameDueTimes_KeepsAll()
    {
        (TimeSpan, long)[] registrations = [(s_earlier, 1), (s_earlier, 2), (s_earlier, 3)];

        SortedSet<(TimeSpan, long)> scheduled = new(
            Comparer<(TimeSpan, long)>.Create(TimerScheduler.CompareDueTimes));
        foreach ((TimeSpan, long) registration in registrations)
        {
            scheduled.Add(registration);
        }

        Assert.That(scheduled, Is.EquivalentTo(registrations));
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
}
