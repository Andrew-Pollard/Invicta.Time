// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;

using NUnit.Framework;

using static Invicta.TimeProviderFixtures;

namespace Invicta;

/// <summary>
/// Behavior that every <see cref="TimeProvider"/> shares, run against both <see cref="TimeProvider.System"/> and
/// <see cref="HighResolutionTimeProvider.Instance"/> so the two cannot drift apart. Ported from the .NET runtime's
/// own TimeProvider and System.Threading.Timer tests.
/// </summary>
[TestFixtureSource(typeof(TimeProviderFixtures), nameof(TimeProviderFixtures.Providers))]
internal sealed class HighResolutionTimeProviderConformanceTests(TimeProvider provider)
{
    private readonly TimeProvider _provider = provider;

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
    public async Task WarmUpProvider()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1), _provider);
    }

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
            _ => fired.TrySetResult(Stopwatch.GetElapsedTime(start)), null, Due, Timeout.InfiniteTimeSpan);

        TimeSpan elapsed = await fired.Task.WaitAsync(CallbackTimeout);
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(Due - TickTolerance));
    }

    [Test]
    public async Task CreateTimer_WithState_PassesStateToCallback()
    {
        object state = new();
        TaskCompletionSource<object?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), state, Due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(CallbackTimeout), Is.SameAs(state));
    }

    [Test]
    public async Task CreateTimer_WithNullState_PassesNullToCallback()
    {
        TaskCompletionSource<object?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            s => observed.TrySetResult(s), null, Due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(CallbackTimeout), Is.Null);
    }

    [TestCaseSource(nameof(OneShotPeriods))]
    public async Task CreateTimer_WithOneShotPeriod_FiresOnce(TimeSpan period)
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Due, period);

        await Task.Delay(Due * 6);
        Assert.That(Volatile.Read(ref count), Is.EqualTo(1));
    }

    [Test]
    [Category(TestCategories.Timing)]
    public async Task CreateTimer_WithPeriod_FiresRepeatedly()
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Due, Period);

        await Task.Delay(Period * 8);
        Assert.That(Volatile.Read(ref count), Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task CreateTimer_WithZeroDueTime_FiresImmediately()
    {
        TaskCompletionSource fired = new();
        using ITimer timer = _provider.CreateTimer(
            _ => fired.TrySetResult(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        await fired.Task.WaitAsync(CallbackTimeout);
    }

    [TestCaseSource(nameof(DueTimesLongerThanTheTest))]
    public async Task CreateTimer_WithDueTimeLongerThanTheTest_DoesNotFire(TimeSpan dueTime)
    {
        int count = 0;
        using ITimer timer = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, dueTime, Timeout.InfiniteTimeSpan);

        await Task.Delay(Due * 4);
        Assert.That(Volatile.Read(ref count), Is.Zero);
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

        await blocking.Task.WaitAsync(CallbackTimeout);

        using (ITimer other = _provider.CreateTimer(
            _ => Interlocked.Increment(ref count), null, Period, Period))
        {
            await Task.Delay(Period * 6);
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

        await done.Task.WaitAsync(CallbackTimeout);

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
                    Due,
                    Timeout.InfiniteTimeSpan);
            }
        });

        await done.Task.WaitAsync(CallbackTimeout);

        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    [Test]
    public void CreateTimer_NullCallback_Throws()
    {
        Assert.That(
            () => _provider.CreateTimer(null!, null, Due, Timeout.InfiniteTimeSpan),
            Throws.ArgumentNullException);
    }

    [TestCaseSource(nameof(InvalidDueTimesAndPeriods))]
    public void CreateTimer_InvalidDueTimeOrPeriod_Throws(TimeSpan dueTime, TimeSpan period)
    {
        Assert.That(
            () => _provider.CreateTimer(_ => { }, null, dueTime, period),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task CreateTimer_WithAsyncLocalValue_FlowsExecutionContextToCallback()
    {
        AsyncLocal<string> local = new() { Value = "flowed" };
        TaskCompletionSource<string?> observed = new();

        using ITimer timer = _provider.CreateTimer(
            _ => observed.TrySetResult(local.Value), null, Due, Timeout.InfiniteTimeSpan);

        Assert.That(await observed.Task.WaitAsync(CallbackTimeout), Is.EqualTo("flowed"));
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
                _ => observed.TrySetResult(local.Value), null, Due, Timeout.InfiniteTimeSpan);
        }

        using (timer)
        {
            Assert.That(await observed.Task.WaitAsync(CallbackTimeout), Is.Null);
        }
    }

    [Test]
    public async Task CreateTimer_WhenUnreferencedAndCollected_KeepsFiring()
    {
        AbandonedTimer abandoned = AbandonedTimer.Start(_provider);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int ticksAfterCollect = abandoned.Ticks;
        await Task.Delay(Period * 2);

        Assert.That(abandoned.Ticks, Is.GreaterThan(ticksAfterCollect));

        abandoned.Stop();
    }

    [Test]
    public async Task CancellationTokenSource_WithDelay_Cancels()
    {
        using CancellationTokenSource cts = new(Due, _provider);
        TaskCompletionSource canceled = new();
        using CancellationTokenRegistration registration = cts.Token.Register(() => canceled.TrySetResult());

        await canceled.Task.WaitAsync(CallbackTimeout);
        Assert.That(cts.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task CancellationTokenSource_WithInfiniteTimeout_DoesNotCancel()
    {
        using CancellationTokenSource cts = new(Timeout.InfiniteTimeSpan, _provider);

        await Task.Delay(Due * 4);
        Assert.That(cts.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task TaskDelay_WithProvider_Completes()
    {
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(Due, _provider);

        Assert.That(Stopwatch.GetElapsedTime(start), Is.GreaterThanOrEqualTo(Due - TickTolerance));
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
        PeriodicTimer periodic = new(Period, _provider);

        Assert.That(await periodic.WaitForNextTickAsync(), Is.True);

        periodic.Dispose();
        Assert.That(await periodic.WaitForNextTickAsync(), Is.False);
    }

    /// <summary>
    /// Counts the ticks of a periodic timer that the test holds only a weak reference to, so the timer can be
    /// collected unless its provider keeps it alive.
    /// </summary>
    private sealed class AbandonedTimer
    {
        private readonly WeakReference<ITimer> _timer;
        private int _ticks;

        /// <summary>Creates a 1 ms periodic timer and keeps only a weak reference to it.</summary>
        /// <param name="provider">The provider to create the timer with.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private AbandonedTimer(TimeProvider provider)
        {
            ITimer timer = provider.CreateTimer(
                static state => Interlocked.Increment(ref ((AbandonedTimer)state!)._ticks),
                this,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1));

            _timer = new WeakReference<ITimer>(timer);
        }

        /// <summary>Gets the number of times the timer has fired.</summary>
        public int Ticks => Volatile.Read(ref _ticks);

        /// <summary>Starts a timer that nothing but its provider references.</summary>
        /// <param name="provider">The provider to create the timer with.</param>
        /// <returns>The object counting the timer's ticks.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static AbandonedTimer Start(TimeProvider provider)
        {
            return new(provider);
        }

        /// <summary>Disposes of the timer, if it still exists.</summary>
        public void Stop()
        {
            if (_timer.TryGetTarget(out ITimer? timer))
            {
                timer.Dispose();
            }
        }
    }
}
