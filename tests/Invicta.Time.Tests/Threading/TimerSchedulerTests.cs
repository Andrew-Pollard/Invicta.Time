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
        using HighResolutionTimer later = NewTimer(s_later);
        using HighResolutionTimer earlier = NewTimer(s_earlier);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TimerScheduler.CompareDueTimes(earlier, later), Is.Negative);
            Assert.That(TimerScheduler.CompareDueTimes(later, earlier), Is.Positive);
        }
    }

    [Test]
    public void CompareDueTimes_SameDueTime_IsNotEqual()
    {
        using HighResolutionTimer first = NewTimer(s_earlier);
        using HighResolutionTimer second = NewTimer(s_earlier);

        int firstToSecond = TimerScheduler.CompareDueTimes(first, second);
        int secondToFirst = TimerScheduler.CompareDueTimes(second, first);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstToSecond, Is.Not.Zero);
            Assert.That(Math.Sign(secondToFirst), Is.EqualTo(-Math.Sign(firstToSecond)));
        }
    }

    [Test]
    public void CompareDueTimes_SameTimer_IsZero()
    {
        using HighResolutionTimer timer = NewTimer(s_earlier);

        Assert.That(TimerScheduler.CompareDueTimes(timer, timer), Is.Zero);
    }

    [Test]
    public void CompareDueTimes_InSortedSetWithSameDueTimes_KeepsAllTimers()
    {
        using HighResolutionTimer first = NewTimer(s_earlier);
        using HighResolutionTimer second = NewTimer(s_earlier);
        using HighResolutionTimer third = NewTimer(s_earlier);
        HighResolutionTimer[] timers = [first, second, third];

        SortedSet<HighResolutionTimer> scheduled = new(
            Comparer<HighResolutionTimer>.Create(TimerScheduler.CompareDueTimes));
        foreach (HighResolutionTimer timer in timers)
        {
            scheduled.Add(timer);
        }

        Assert.That(scheduled, Is.EquivalentTo(timers));
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

    private static HighResolutionTimer NewTimer(TimeSpan dueTime) =>
        new(static _ => { }, null, null) { DueTime = dueTime };
}
