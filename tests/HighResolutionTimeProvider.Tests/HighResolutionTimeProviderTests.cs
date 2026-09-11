// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

// Latency assertions are unreliable when tests compete for the thread pool.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Invicta;

public class HighResolutionTimeProviderTests
{
    private static readonly TimeProvider s_provider = TimeProvider.HighResolution;

    [Fact]
    public void IsSupportedOnThisMachine() => Assert.True(HighResolutionTimeProvider.IsSupported);

    [Fact]
    public void HighResolution_ReturnsSharedHighResolutionTimeProvider()
    {
        Assert.IsType<HighResolutionTimeProvider>(TimeProvider.HighResolution);
        Assert.Same(TimeProvider.HighResolution, TimeProvider.HighResolution);
    }

    [Fact]
    public async Task TaskDelay_OneMillisecond_IsFarBelowSystemTickResolution()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1), s_provider); // warm up the scheduler thread

        double[] samples = new double[50];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromMilliseconds(1), s_provider);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(samples);
        Assert.True(samples[0] >= 1.0, $"Fired early: min {samples[0]:F3} ms");
        Assert.True(samples[samples.Length / 2] < 5.0, $"Median {samples[samples.Length / 2]:F3} ms");
    }

    [Fact]
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

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(3), $"Fired after {elapsed.TotalMilliseconds:F3} ms");
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ZeroDueTime_FiresImmediately()
    {
        var fired = new TaskCompletionSource();
        using ITimer timer = s_provider.CreateTimer(
            _ => fired.TrySetResult(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Periodic_OneMillisecond_FiresAtRoughlyOneKilohertz()
    {
        int count = 0;
        using ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        await Task.Delay(500);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        int fired = Volatile.Read(ref count);

        // A 15.6 ms tick-based timer would manage ~32 in 500 ms.
        Assert.InRange(fired, 300, 520);
    }

    [Fact]
    public async Task Change_ToInfinite_StopsTimer_AndCanRestart()
    {
        int count = 0;
        using ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        await Task.Delay(20); // let in-flight callbacks drain
        int stopped = Volatile.Read(ref count);
        await Task.Delay(50);
        Assert.Equal(stopped, Volatile.Read(ref count));

        Assert.True(timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan));
        await Task.Delay(50);
        Assert.Equal(stopped + 1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Change_Reschedules_EarlierAndLater()
    {
        var fired = new TaskCompletionSource<TimeSpan>();
        long start = Stopwatch.GetTimestamp();
        using ITimer timer = s_provider.CreateTimer(
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)),
            null,
            TimeSpan.FromHours(1),
            Timeout.InfiniteTimeSpan);

        timer.Change(TimeSpan.FromMilliseconds(2), Timeout.InfiniteTimeSpan);
        TimeSpan elapsed = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Dispose_StopsCallbacks_AndChangeReturnsFalse()
    {
        int count = 0;
        ITimer timer = s_provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);

        timer.Dispose();
        int afterDispose = Volatile.Read(ref count);
        await Task.Delay(50);

        Assert.Equal(afterDispose, Volatile.Read(ref count));
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        timer.Dispose(); // idempotent
    }

    [Fact]
    public async Task DisposeAsync_WaitsForRunningCallback()
    {
        var entered = new TaskCompletionSource();
        var release = new ManualResetEventSlim();
        bool finished = false;

        ITimer timer = s_provider.CreateTimer(
            _ =>
            {
                entered.TrySetResult();
                release.Wait();
                Volatile.Write(ref finished, true);
            },
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ValueTask disposal = timer.DisposeAsync();
        Assert.False(disposal.IsCompleted);

        release.Set();
        await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public async Task ExecutionContext_FlowsToCallback()
    {
        var local = new AsyncLocal<string> { Value = "flowed" };
        var observed = new TaskCompletionSource<string?>();

        using ITimer timer = s_provider.CreateTimer(
            _ => observed.TrySetResult(local.Value), null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

        Assert.Equal("flowed", await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExecutionContext_NotFlowedWhenSuppressed()
    {
        var local = new AsyncLocal<string> { Value = "flowed" };
        var observed = new TaskCompletionSource<string?>();

        ITimer timer;
        using (ExecutionContext.SuppressFlow())
        {
            timer = s_provider.CreateTimer(
                _ => observed.TrySetResult(local.Value),
                null,
                TimeSpan.FromMilliseconds(1),
                Timeout.InfiniteTimeSpan);
        }

        using (timer)
        {
            Assert.Null(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
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
        Assert.Equal(0, Volatile.Read(ref early));

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    [Fact]
    public async Task CancellationTokenSource_WithProvider_CancelsOnTime()
    {
        long start = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2), s_provider);
        var cancelled = new TaskCompletionSource<TimeSpan>();
        cts.Token.Register(() => cancelled.TrySetResult(Stopwatch.GetElapsedTime(start)));

        TimeSpan elapsed = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(elapsed < TimeSpan.FromMilliseconds(10), $"Cancelled after {elapsed.TotalMilliseconds:F3} ms");
    }

    [Fact]
    public async Task PeriodicTimer_WithProvider_Ticks()
    {
        using var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(1), s_provider);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(await periodic.WaitForNextTickAsync());
        }

        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task UnreferencedTimer_IsCollectedAndStops()
    {
        var counter = new StrongBox<int>();
        CreateAbandonedTimer(counter);

        await Task.Delay(20);
        Assert.True(Volatile.Read(ref counter.Value) > 0);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(20);

        int afterCollect = Volatile.Read(ref counter.Value);
        await Task.Delay(50);
        Assert.Equal(afterCollect, Volatile.Read(ref counter.Value));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => s_provider.CreateTimer(null!, null, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => s_provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(-2), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => s_provider.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(-2)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => s_provider.CreateTimer(_ => { }, null, TimeSpan.FromDays(50), TimeSpan.Zero));

        using ITimer timer = s_provider.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Change(TimeSpan.FromMilliseconds(-2), TimeSpan.Zero));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAbandonedTimer(StrongBox<int> counter) =>
        s_provider.CreateTimer(
            static s => Interlocked.Increment(ref ((StrongBox<int>)s!).Value),
            counter,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
}
