// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;

using NUnit.Framework;

using static Invicta.TimeProviderFixtures;

namespace Invicta.Threading;

[TestFixtureSource(typeof(TimeProviderFixtures), nameof(TimeProviderFixtures.Providers))]
internal sealed class HighResolutionTimerTests(TimeProvider provider)
{
    private readonly TimeProvider _provider = provider;

    [OneTimeSetUp]
    public async Task WarmUpProvider()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1), _provider);
    }

    [Test]
    public async Task Change_ToInfiniteDueTime_StopsFiring()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Due, Period);

        await Task.Delay(Period * 4);
        Assert.That(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan), Is.True);

        await Task.Delay(Period);
        int stopped = Volatile.Read(ref count);
        await Task.Delay(Period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(stopped));
    }

    [Test]
    public async Task Change_BeforeDueTime_Reschedules()
    {
        TaskCompletionSource<TimeSpan> fired = new();
        long start = Stopwatch.GetTimestamp();

        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)),
            null,
            TimeSpan.FromHours(1),
            Timeout.InfiniteTimeSpan);

        Assert.That(timer.Change(Due, Timeout.InfiniteTimeSpan), Is.True);

        TimeSpan elapsed = await fired.Task.WaitAsync(CallbackTimeout);
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task Change_FromInsideCallback_TakesEffect()
    {
        TimeSpan longPeriod = Period * 4;
        StrongBox<ITimer?> self = new();
        TaskCompletionSource<TimeSpan> thirdTick = new();
        int count = 0;
        long secondTick = 0;

        using ITimer timer = _provider.CreateTimer(
            _ =>
            {
                switch (Interlocked.Increment(ref count))
                {
                    case 2:
                        Volatile.Write(ref secondTick, Stopwatch.GetTimestamp());
                        self.Value!.Change(longPeriod, longPeriod);
                        break;

                    case 3:
                        thirdTick.TrySetResult(Stopwatch.GetElapsedTime(Volatile.Read(ref secondTick)));
                        break;

                    default:
                        break;
                }
            },
            null,
            Period,
            Period);

        self.Value = timer;

        TimeSpan gap = await thirdTick.Task.WaitAsync(CallbackTimeout);
        Assert.That(gap, Is.GreaterThanOrEqualTo(longPeriod - TickTolerance));
    }

    [Test]
    public async Task Change_AfterOneShotFired_RestartsTimer()
    {
        TaskCompletionSource first = new();
        TaskCompletionSource second = new();
        int count = 0;

        using ITimer timer = _provider.CreateTimer(
            _ =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    first.TrySetResult();
                }
                else
                {
                    second.TrySetResult();
                }
            },
            null,
            Due,
            Timeout.InfiniteTimeSpan);

        await first.Task.WaitAsync(CallbackTimeout);
        Assert.That(timer.Change(Due, Timeout.InfiniteTimeSpan), Is.True);
        await second.Task.WaitAsync(CallbackTimeout);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(2));
    }

    [Test]
    public void Change_AfterDispose_ReturnsFalse()
    {
        ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        timer.Dispose();
        Assert.That(timer.Change(Due, Timeout.InfiniteTimeSpan), Is.False);
    }

    [Test]
    public void Change_InvalidDueTimeOrPeriod_Throws()
    {
        using ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => timer.Change(TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => timer.Change(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(-2)),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }

    [Test]
    public async Task Dispose_FromInsideCallback_StopsTimer()
    {
        StrongBox<ITimer?> self = new();
        TaskCompletionSource fired = new();
        int count = 0;

        ITimer timer = _provider.CreateTimer(
            _ =>
            {
                Interlocked.Increment(ref count);
                self.Value!.Dispose();
                fired.TrySetResult();
            },
            null,
            Due,
            Period);

        self.Value = timer;

        await fired.Task.WaitAsync(CallbackTimeout);
        await Task.Delay(Period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    public async Task Dispose_PeriodicTimer_StopsCallbacks()
    {
        int count = 0;
        ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Due, Period);

        await Task.Delay(Period * 4);
        timer.Dispose();

        await Task.Delay(Period);
        int afterDispose = Volatile.Read(ref count);
        await Task.Delay(Period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(afterDispose));
    }

    [Test]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timer.Dispose, Throws.Nothing);
            Assert.That(timer.Dispose, Throws.Nothing);
        }
    }

    [Test]
    public async Task DisposeAsync_WhileCallbackIsRunning_WaitsForCallback()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new();
        bool finished = false;

        ITimer timer = _provider.CreateTimer(
            _ =>
            {
                entered.TrySetResult();
                release.Wait();
                Volatile.Write(ref finished, true);
            },
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);

        await entered.Task.WaitAsync(CallbackTimeout);

        ValueTask disposal = timer.DisposeAsync();
        Assert.That(disposal.IsCompleted, Is.False);

        release.Set();
        await disposal.AsTask().WaitAsync(CallbackTimeout);
        Assert.That(Volatile.Read(ref finished), Is.True);
    }

    [Test]
    public async Task DisposeAsync_CalledTwice_Completes()
    {
        ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        await timer.DisposeAsync();
        await timer.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_AfterDispose_Completes()
    {
        ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        timer.Dispose();
        await timer.DisposeAsync();
    }
}
