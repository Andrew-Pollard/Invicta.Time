// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

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

    private static IEnumerable<TestCaseData> OneShotPeriods()
    {
        yield return new TestCaseData(Timeout.InfiniteTimeSpan).SetArgDisplayNames("Infinite");
        yield return new TestCaseData(TimeSpan.Zero).SetArgDisplayNames("Zero");
    }

    private static IEnumerable<TestCaseData> DueTimesLongerThanTheTest()
    {
        yield return new TestCaseData(Timeout.InfiniteTimeSpan).SetArgDisplayNames("Infinite");
        yield return new TestCaseData(TimeSpan.FromHours(1)).SetArgDisplayNames("OneHour");
    }

    private static IEnumerable<TestCaseData> InvalidDueTimesAndPeriods()
    {
        yield return new TestCaseData(TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan)
            .SetArgDisplayNames("NegativeDueTime");
        yield return new TestCaseData(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(-2))
            .SetArgDisplayNames("NegativePeriod");
        yield return new TestCaseData(TimeSpan.FromDays(50), Timeout.InfiniteTimeSpan)
            .SetArgDisplayNames("DueTimeTooLong");
    }

    [OneTimeSetUp]
    public async Task WarmUpProvider() => await Task.Delay(TimeSpan.FromMilliseconds(1), _provider);

    [Test]
    public void GetUtcNow_ComparedWithSystemClock_LiesBetweenSurroundingReadings()
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
    public void GetTimestamp_ComparedWithStopwatch_Agrees()
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
    public async Task CreateTimer_WithDueTime_FiresAfterDueTime()
    {
        TaskCompletionSource<TimeSpan> fired = new();
        long start = Stopwatch.GetTimestamp();

        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)), null, s_due, Timeout.InfiniteTimeSpan);

        TimeSpan elapsed = await fired.Task.WaitAsync(s_timeout);
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(s_due - s_tickTolerance));
    }

    [Test]
    public async Task CreateTimer_WithState_PassesStateToCallback()
    {
        object state = new();
        TaskCompletionSource<object?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), state, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.SameAs(state));
    }

    [Test]
    public async Task CreateTimer_WithNullState_PassesNullToCallback()
    {
        TaskCompletionSource<object?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), null, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.Null);
    }

    [TestCaseSource(nameof(OneShotPeriods))]
    public async Task CreateTimer_WithOneShotPeriod_FiresOnce(TimeSpan period)
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, period);

        await Task.Delay(s_due * 6);
        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CreateTimer_WithPeriod_FiresRepeatedly()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, s_due, s_period);

        await Task.Delay(s_period * 8);
        Assert.That(Volatile.Read(ref count), Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task CreateTimer_WithZeroDueTime_FiresImmediately()
    {
        TaskCompletionSource fired = new();
        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        await fired.Task.WaitAsync(s_timeout);
    }

    [TestCaseSource(nameof(DueTimesLongerThanTheTest))]
    public async Task CreateTimer_WithDueTimeLongerThanTheTest_DoesNotFire(TimeSpan dueTime)
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, dueTime, Timeout.InfiniteTimeSpan);

        await Task.Delay(s_due * 4);
        Assert.That(Volatile.Read(ref count), Is.Zero);
    }

    [Test]
    public async Task Change_ToInfiniteDueTime_StopsFiring()
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
    public async Task Change_BeforeDueTime_Reschedules()
    {
        TaskCompletionSource<TimeSpan> fired = new();
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
    public async Task Change_FromInsideCallback_TakesEffect()
    {
        TimeSpan longPeriod = s_period * 4;
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
            s_period,
            s_period);

        self.Value = timer;

        TimeSpan gap = await thirdTick.Task.WaitAsync(s_timeout);
        Assert.That(gap, Is.GreaterThanOrEqualTo(longPeriod - s_tickTolerance));
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
            s_due,
            s_period);

        self.Value = timer;

        await fired.Task.WaitAsync(s_timeout);
        await Task.Delay(s_period * 4);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
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
            s_due,
            Timeout.InfiniteTimeSpan);

        await first.Task.WaitAsync(s_timeout);
        Assert.That(timer.Change(s_due, Timeout.InfiniteTimeSpan), Is.True);
        await second.Task.WaitAsync(s_timeout);

        Assert.That(Volatile.Read(ref count), Is.EqualTo(2));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CreateTimer_WhileAnotherCallbackIsBlocked_StillFires()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource blocking = new();
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
    public async Task CreateTimer_ManyTimers_AllFire()
    {
        const int TimerCount = 100;

        int remaining = TimerCount;
        TaskCompletionSource done = new();

        Random random = new(42);
        ITimer[] timers = new ITimer[TimerCount];
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
    public async Task CreateTimer_FromManyThreadsConcurrently_AllFire()
    {
        const int Threads = 8;
        const int PerThread = 10;

        int remaining = Threads * PerThread;
        TaskCompletionSource done = new();

        ITimer[] timers = new ITimer[Threads * PerThread];
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
    public async Task Dispose_PeriodicTimer_StopsCallbacks()
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

        await entered.Task.WaitAsync(s_timeout);

        ValueTask disposal = timer.DisposeAsync();
        Assert.That(disposal.IsCompleted, Is.False);

        release.Set();
        await disposal.AsTask().WaitAsync(s_timeout);
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

    [TestCaseSource(nameof(InvalidDueTimesAndPeriods))]
    public void CreateTimer_InvalidDueTimeOrPeriod_Throws(TimeSpan dueTime, TimeSpan period) =>
        Assert.That(
            () => _provider.CreateTimer(_ => { }, null, dueTime, period),
            Throws.TypeOf<ArgumentOutOfRangeException>());

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
    public async Task CreateTimer_WithAsyncLocalValue_FlowsExecutionContextToCallback()
    {
        AsyncLocal<string> local = new() { Value = "flowed" };
        TaskCompletionSource<string?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            _ => observed.TrySetResult(local.Value), null, s_due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(s_timeout), Is.EqualTo("flowed"));
    }

    [Test]
    public async Task CreateTimer_WithFlowSuppressed_DoesNotFlowExecutionContext()
    {
        AsyncLocal<string> local = new() { Value = "flowed" };
        TaskCompletionSource<string?> observed = new();

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
        using CancellationTokenSource cts = new(s_due, _provider);
        TaskCompletionSource canceled = new();
        using CancellationTokenRegistration registration = cts.Token.Register(() => canceled.TrySetResult());

        await canceled.Task.WaitAsync(s_timeout);
        Assert.That(cts.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task CancellationTokenSource_WithInfiniteTimeout_DoesNotCancel()
    {
        using CancellationTokenSource cts = new(Timeout.InfiniteTimeSpan, _provider);

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
        using CancellationTokenSource cts = new();
        Task delay = Task.Delay(TimeSpan.FromMinutes(1), _provider, cts.Token);

        await cts.CancelAsync();

        await Assert.ThatAsync(() => delay, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task WaitForNextTickAsync_BeforeAndAfterDispose_ReturnsTrueThenFalse()
    {
        PeriodicTimer periodic = new(s_period, _provider);

        Assert.That(await periodic.WaitForNextTickAsync(), Is.True);

        periodic.Dispose();
        Assert.That(await periodic.WaitForNextTickAsync(), Is.False);
    }
}
