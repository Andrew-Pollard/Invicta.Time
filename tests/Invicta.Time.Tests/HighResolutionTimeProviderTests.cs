// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using NUnit.Framework;

// Latency assertions are unreliable when tests compete for the thread pool, so never run tests in parallel.
[assembly: Parallelizable(ParallelScope.None)]

namespace Invicta;

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
    public void IsSupportedOnThisMachine() => Assert.That(HighResolutionTimeProvider.IsSupported, Is.True);

    [Test]
    public void Instance_IsSharedAndTheOnlyWayToGetOne()
    {
        HighResolutionTimeProvider first = HighResolutionTimeProvider.Instance;
        HighResolutionTimeProvider second = HighResolutionTimeProvider.Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(typeof(HighResolutionTimeProvider).IsSealed, Is.True);
            Assert.That(typeof(HighResolutionTimeProvider).GetConstructors(), Is.Empty);
        }
    }

    [Test]
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
    public async Task OneShot_FiresOnceAndNeverBeforeDueTime()
    {
        int count = 0;
        var fired = new TaskCompletionSource<TimeSpan>();
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
    public async Task Periodic_OneMillisecond_FiresAtRoughlyOneKilohertz()
    {
        int count = 0;
        using ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        await Task.Delay(500);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        int fired = Volatile.Read(ref count);

        // A 15.6 ms tick-based timer would manage ~32 in 500 ms.
        Assert.That(fired, Is.InRange(300, 520));
    }

    [Test]
    public async Task ManyTimers_EachFiresOnceAndNotEarly()
    {
        const int TimerCount = 500;
        var random = new Random(42);
        int early = 0;
        int remaining = TimerCount;
        var done = new TaskCompletionSource();
        var timers = new ITimer[TimerCount];

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
    public async Task CancellationTokenSource_WithProvider_CancelsOnTime()
    {
        long start = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2), s_provider);
        var canceled = new TaskCompletionSource<TimeSpan>();
        cts.Token.Register(() => canceled.TrySetResult(Stopwatch.GetElapsedTime(start)));

        TimeSpan elapsed = await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromMilliseconds(10)));
    }

    [Test]
    public async Task PeriodicTimer_WithProvider_Ticks()
    {
        using var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(1), s_provider);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 20; i++)
        {
            Assert.That(await periodic.WaitForNextTickAsync(), Is.True);
        }

        Assert.That(Stopwatch.GetElapsedTime(start), Is.LessThan(TimeSpan.FromMilliseconds(100)));
    }

    [Test]
    public async Task UnreferencedTimer_IsCollectedAndStops()
    {
        var counter = new StrongBox<int>();
        CreateAbandonedTimer(counter);

        await Task.Delay(20);
        Assert.That(Volatile.Read(ref counter.Value), Is.Positive);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(20);

        int afterCollect = Volatile.Read(ref counter.Value);
        await Task.Delay(50);
        Assert.That(Volatile.Read(ref counter.Value), Is.EqualTo(afterCollect));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAbandonedTimer(StrongBox<int> counter) =>
        s_provider.CreateTimer(
            static s => Interlocked.Increment(ref ((StrongBox<int>)s!).Value),
            counter,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
}
