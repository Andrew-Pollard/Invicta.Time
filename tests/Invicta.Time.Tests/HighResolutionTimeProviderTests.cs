// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using NUnit.Framework;

namespace Invicta;

/// <summary>
/// Tests behavior specific to <see cref="HighResolutionTimeProvider"/>, such as its resolution, which
/// <see cref="TimeProvider.System"/> does not share.
/// </summary>
internal sealed class HighResolutionTimeProviderTests
{
    private static readonly TimeProvider s_provider = HighResolutionTimeProvider.Instance;

    [OneTimeSetUp]
    public async Task WarmUpScheduler()
    {
        // Start the scheduler thread and JIT the timer path up front, so whichever test runs first isn't
        // measuring one-off start-up cost.
        await Task.Delay(TimeSpan.FromMilliseconds(1), s_provider);
    }

    [Test]
    public void IsSupported_OnThisMachine_IsTrue()
    {
        Assert.That(HighResolutionTimeProvider.IsSupported, Is.True);
    }

    [Test]
    public void Instance_ReadTwice_ReturnsTheOnlyInstance()
    {
        HighResolutionTimeProvider first = HighResolutionTimeProvider.Instance;
        HighResolutionTimeProvider second = HighResolutionTimeProvider.Instance;

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task TaskDelay_OneMillisecond_IsFarBelowSystemTickResolution()
    {
        double[] samples = new double[50];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromMilliseconds(1), s_provider);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(samples);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(samples[0], Is.GreaterThanOrEqualTo(1.0), "Minimum delay (ms); below 1 is early");
            Assert.That(samples[samples.Length / 2], Is.LessThan(5.0), "Median delay (ms)");
        }
    }

    [Test]
    public async Task CreateTimer_OneShot_FiresOnceAndNeverBeforeDueTime()
    {
        int count = 0;
        TaskCompletionSource<TimeSpan> fired = new();
        long start = Stopwatch.GetTimestamp();

        using ITimer timer = s_provider.CreateTimer(
            _ =>
            {
                Interlocked.Increment(ref count);
                fired.TrySetResult(Stopwatch.GetElapsedTime(start));
            },
            null,
            TimeSpan.FromMilliseconds(3),
            Timeout.InfiniteTimeSpan);

        TimeSpan elapsed = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(3)));
            Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
        }
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CreateTimer_OneMillisecondPeriod_FiresAtRoughlyOneKilohertz()
    {
        int count = 0;
        using ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count),
            null,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        await Task.Delay(500);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        int fired = Volatile.Read(ref count);

        // A 15.6 ms tick-based timer would manage ~32 in 500 ms.
        Assert.That(fired, Is.InRange(300, 520));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CreateTimer_SubMillisecondPeriod_Repeats()
    {
        int count = 0;
        using ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(0.5));

        await Task.Delay(50);

        // TimeProvider.System truncates the period to zero milliseconds and fires once.
        Assert.That(Volatile.Read(ref count), Is.GreaterThan(1));
    }

    [Test]
    public void CreateTimer_NegativeSubMillisecondDueTime_Throws()
    {
        // TimeProvider.System truncates the due time to zero milliseconds and fires immediately.
        Assert.That(
            () => s_provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(-0.5), Timeout.InfiniteTimeSpan),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task CreateTimer_ManyRandomDueTimes_EachFiresOnceAndNotEarly()
    {
        const int TimerCount = 500;

        int early = 0;
        int remaining = TimerCount;
        TaskCompletionSource done = new();

        Random random = new(42);
        ITimer[] timers = new ITimer[TimerCount];
        for (int i = 0; i < TimerCount; i++)
        {
            TimeSpan due = TimeSpan.FromTicks(random.Next(0, 200_000)); // 0-20 ms
            long start = Stopwatch.GetTimestamp();
            timers[i] = s_provider.CreateTimer(
                _ =>
                {
                    if (Stopwatch.GetElapsedTime(start) < due)
                    {
                        Interlocked.Increment(ref early);
                    }

                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        done.TrySetResult();
                    }
                },
                null,
                due,
                Timeout.InfiniteTimeSpan);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(Volatile.Read(ref early), Is.Zero, "Timers that fired before their due time");

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CancellationTokenSource_WithProvider_CancelsOnTime()
    {
        long start = Stopwatch.GetTimestamp();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(2), s_provider);
        TaskCompletionSource<TimeSpan> canceled = new();
        cts.Token.Register(() => canceled.TrySetResult(Stopwatch.GetElapsedTime(start)));

        TimeSpan elapsed = await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromMilliseconds(10)));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task WaitForNextTickAsync_OneMillisecondPeriod_TicksFarFasterThanSystemTick()
    {
        using PeriodicTimer periodic = new(TimeSpan.FromMilliseconds(1), s_provider);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 20; i++)
        {
            Assert.That(await periodic.WaitForNextTickAsync(), Is.True);
        }

        Assert.That(Stopwatch.GetElapsedTime(start), Is.LessThan(TimeSpan.FromMilliseconds(100)));
    }
}
