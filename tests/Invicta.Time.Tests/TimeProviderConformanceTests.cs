// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Invicta;

/// <summary>
/// Behavior that every <see cref="TimeProvider"/> shares, run against both <see cref="TimeProvider.System"/> and
/// <see cref="HighResolutionTimeProvider.Instance"/> so the two cannot drift apart. Ported from the .NET runtime's
/// own TimeProvider and System.Threading.Timer tests.
/// </summary>
[TestFixtureSource(nameof(Providers))]
internal sealed class TimeProviderConformanceTests(TimeProvider provider)
{
    // Comfortably longer than the ~15.6 ms tick that TimeProvider.System's timers are limited to.
    private static readonly TimeSpan s_due = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_period = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    // TimeProvider.System rounds to its tick, so a callback can arrive just before Stopwatch agrees the due time
    // has elapsed. Timing assertions allow for that.
    private static readonly TimeSpan s_tickTolerance = TimeSpan.FromMilliseconds(16);

    private readonly TimeProvider _provider = provider;

    private static IEnumerable<TestFixtureData> Providers()
    {
        yield return new TestFixtureData(TimeProvider.System).SetArgDisplayNames("System");
        yield return new TestFixtureData(HighResolutionTimeProvider.Instance).SetArgDisplayNames("HighResolution");
    }

    [OneTimeSetUp]
    public async Task WarmUpProvider() => await Task.Delay(TimeSpan.FromMilliseconds(1), _provider);

    [Test]
    public void GetUtcNow_LiesBetweenSurroundingReadings()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset now = _provider.GetUtcNow();
        DateTimeOffset after = DateTimeOffset.UtcNow;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(now, Is.InRange(before, after));
            Assert.That(now.Offset, Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public void GetTimestamp_AgreesWithStopwatch()
    {
        long before = Stopwatch.GetTimestamp();
        long timestamp = _provider.GetTimestamp();
        long after = Stopwatch.GetTimestamp();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timestamp, Is.InRange(before, after));
            Assert.That(_provider.TimestampFrequency, Is.EqualTo(Stopwatch.Frequency));
            Assert.That(
                _provider.GetElapsedTime(before, after),
                Is.EqualTo(Stopwatch.GetElapsedTime(before, after)));
        }
    }

    [Test]
    public async Task Timer_FiresAfterDueTime()
    {
        var fired = new TaskCompletionSource<TimeSpan>();
        long start = Stopwatch.GetTimestamp();

        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)), null, s_due, Timeout.InfiniteTimeSpan);

        TimeSpan elapsed = await fired.Task.WaitAsync(s_timeout);
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(s_due - s_tickTolerance));
    }

    [Test]
    public async Task Timer_PassesStateToCallback()
    {
        object state = new();
        var observed = new TaskCompletionSource<object?>();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), state, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.SameAs(state));
    }

    [Test]
    public async Task Timer_PassesNullStateToCallback()
    {
        var observed = new TaskCompletionSource<object?>();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), null, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.Null);
    }

    [Test]
    public async Task Timer_WithInfinitePeriod_FiresOnce()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, Timeout.InfiniteTimeSpan);

        await Task.Delay(s_due * 6);
        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    public async Task Timer_WithZeroPeriod_FiresOnce()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, TimeSpan.Zero);

        await Task.Delay(s_due * 6);
        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    public async Task Timer_WithPeriod_FiresRepeatedly()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, s_period);

        await Task.Delay(s_period * 8);
        Assert.That(Volatile.Read(ref count), Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task Timer_WithZeroDueTime_FiresImmediately()
    {
        var fired = new TaskCompletionSource();
        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        await fired.Task.WaitAsync(s_timeout);
    }

    [Test]
    public async Task Timer_WithInfiniteDueTime_NeverFires()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        await Task.Delay(s_due * 4);
        Assert.That(Volatile.Read(ref count), Is.Zero);
    }

    [Test]
    public async Task Timer_WithLongDueTime_DoesNotFireEarly()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);

        await Task.Delay(s_due * 4);
        Assert.That(Volatile.Read(ref count), Is.Zero);
    }

    [Test]
    public async Task Timer_ChangeToInfinite_StopsFiring()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, s_period);

        await Task.Delay(s_period * 4);
        Assert.That(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan), Is.True);

        await Task.Delay(s_period);
        int stopped = Volatile.Read(ref count);
        await Task.Delay(s_period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(stopped));
    }

    [Test]
    public async Task Timer_ChangeBeforeDueTime_Reschedules()
    {
        var fired = new TaskCompletionSource<TimeSpan>();
        long start = Stopwatch.GetTimestamp();

        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)),
            null,
            TimeSpan.FromHours(1),
            Timeout.InfiniteTimeSpan);

        Assert.That(timer.Change(s_due, Timeout.InfiniteTimeSpan), Is.True);
        TimeSpan elapsed = await fired.Task.WaitAsync(s_timeout);
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task Timer_ChangePeriodFromInsideCallback_TakesEffect()
    {
        TimeSpan longPeriod = s_period * 4;
        var self = new StrongBox<ITimer?>();
        var thirdTick = new TaskCompletionSource<TimeSpan>();
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
            s_period,
            s_period);
        self.Value = timer;

        TimeSpan gap = await thirdTick.Task.WaitAsync(s_timeout);
        Assert.That(gap, Is.GreaterThanOrEqualTo(longPeriod - s_tickTolerance));
    }

    [Test]
    public async Task Timer_CanDisposeItselfInsideCallback()
    {
        var self = new StrongBox<ITimer?>();
        var fired = new TaskCompletionSource();
        int count = 0;

        ITimer timer = _provider.CreateTimer(
            _ =>
            {
                Interlocked.Increment(ref count);
                self.Value!.Dispose();
                fired.TrySetResult();
            },
            null,
            s_due,
            s_period);
        self.Value = timer;

        await fired.Task.WaitAsync(s_timeout);
        await Task.Delay(s_period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    public async Task Timer_AfterFiring_CanBeRestartedWithChange()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
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
            s_due,
            Timeout.InfiniteTimeSpan);

        await first.Task.WaitAsync(s_timeout);
        Assert.That(timer.Change(s_due, Timeout.InfiniteTimeSpan), Is.True);
        await second.Task.WaitAsync(s_timeout);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(2));
    }

    [Test]
    public async Task Timer_BlockedCallback_DoesNotBlockOtherTimers()
    {
        using var release = new ManualResetEventSlim();
        var blocking = new TaskCompletionSource();
        int count = 0;

        using ITimer blocked = _provider.CreateTimer(
            _ =>
            {
                blocking.TrySetResult();
                release.Wait();
            },
            null,
            TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);

        await blocking.Task.WaitAsync(s_timeout);

        using (ITimer other = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_period, s_period))
        {
            await Task.Delay(s_period * 6);
        }

        release.Set();
        Assert.That(Volatile.Read(ref count), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task ManyTimers_AllFire()
    {
        const int TimerCount = 100;
        var random = new Random(42);
        var done = new TaskCompletionSource();
        int remaining = TimerCount;
        var timers = new ITimer[TimerCount];

        for (int i = 0; i < TimerCount; i++)
        {
            timers[i] = _provider.CreateTimer(
                _ =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        done.TrySetResult();
                    }
                },
                null,
                TimeSpan.FromMilliseconds(random.Next(0, 100)),
                Timeout.InfiniteTimeSpan);
        }

        await done.Task.WaitAsync(s_timeout);

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    [Test]
    public async Task TimersCreatedConcurrently_AllFire()
    {
        const int Threads = 8;
        const int PerThread = 10;
        var done = new TaskCompletionSource();
        int remaining = Threads * PerThread;
        var timers = new ITimer[Threads * PerThread];

        Parallel.For(0, Threads, t =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                timers[(t * PerThread) + i] = _provider.CreateTimer(
                    _ =>
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            done.TrySetResult();
                        }
                    },
                    null,
                    s_due,
                    Timeout.InfiniteTimeSpan);
            }
        });

        await done.Task.WaitAsync(s_timeout);

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    [Test]
    public async Task Dispose_StopsCallbacks()
    {
        int count = 0;
        ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, s_period);

        await Task.Delay(s_period * 4);
        timer.Dispose();
        await Task.Delay(s_period);
        int afterDispose = Volatile.Read(ref count);
        await Task.Delay(s_period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(afterDispose));
    }

    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
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
    public async Task DisposeAsync_WaitsForRunningCallback()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
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

        await entered.Task.WaitAsync(s_timeout);
        ValueTask disposal = timer.DisposeAsync();
        Assert.That(disposal.IsCompleted, Is.False);

        release.Set();
        await disposal.AsTask().WaitAsync(s_timeout);
        Assert.That(Volatile.Read(ref finished), Is.True);
    }

    [Test]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
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

    [Test]
    public void Change_AfterDispose_ReturnsFalse()
    {
        ITimer timer = _provider.CreateTimer(
            _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Dispose();

        Assert.That(timer.Change(s_due, Timeout.InfiniteTimeSpan), Is.False);
    }

    [Test]
    public void CreateTimer_NullCallback_Throws() =>
        Assert.That(
            () => _provider.CreateTimer(null!, null, s_due, Timeout.InfiniteTimeSpan),
            Throws.ArgumentNullException);

    [Test]
    public void CreateTimer_InvalidDueTimeOrPeriod_Throws()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => _provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => _provider.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(-2)),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => _provider.CreateTimer(_ => { }, null, TimeSpan.FromDays(50), Timeout.InfiniteTimeSpan),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
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
    public async Task ExecutionContext_FlowsToCallback()
    {
        var local = new AsyncLocal<string> { Value = "flowed" };
        var observed = new TaskCompletionSource<string?>();

        using ITimer timer = _provider.CreateTimer(
            _ => observed.TrySetResult(local.Value), null, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.EqualTo("flowed"));
    }

    [Test]
    public async Task ExecutionContext_NotFlowedWhenSuppressed()
    {
        var local = new AsyncLocal<string> { Value = "flowed" };
        var observed = new TaskCompletionSource<string?>();

        ITimer timer;
        using (ExecutionContext.SuppressFlow())
        {
            timer = _provider.CreateTimer(
                _ => observed.TrySetResult(local.Value), null, s_due, Timeout.InfiniteTimeSpan);
        }

        using (timer)
        {
            Assert.That(await observed.Task.WaitAsync(s_timeout), Is.Null);
        }
    }

    [Test]
    public async Task CancellationTokenSource_WithDelay_Cancels()
    {
        using var cts = new CancellationTokenSource(s_due, _provider);
        var canceled = new TaskCompletionSource();
        using CancellationTokenRegistration registration = cts.Token.Register(() => canceled.TrySetResult());

        await canceled.Task.WaitAsync(s_timeout);
        Assert.That(cts.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task CancellationTokenSource_WithInfiniteTimeout_DoesNotCancel()
    {
        using var cts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _provider);

        await Task.Delay(s_due * 4);
        Assert.That(cts.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task TaskDelay_WithProvider_Completes()
    {
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(s_due, _provider);

        Assert.That(Stopwatch.GetElapsedTime(start), Is.GreaterThanOrEqualTo(s_due - s_tickTolerance));
    }

    [Test]
    public async Task TaskDelay_WithProvider_CanBeCanceled()
    {
        using var cts = new CancellationTokenSource();
        Task delay = Task.Delay(TimeSpan.FromMinutes(1), _provider, cts.Token);

        await cts.CancelAsync();

        await Assert.ThatAsync(() => delay, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task PeriodicTimer_Ticks_AndStopsAfterDispose()
    {
        var periodic = new PeriodicTimer(s_period, _provider);

        Assert.That(await periodic.WaitForNextTickAsync(), Is.True);

        periodic.Dispose();
        Assert.That(await periodic.WaitForNextTickAsync(), Is.False);
    }
}
