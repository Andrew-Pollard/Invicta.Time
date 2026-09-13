// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics.CodeAnalysis;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;

using Perfolizer.Horology;
using Perfolizer.Mathematics.OutlierDetection;

namespace Invicta;

/// <summary>
/// Benchmarks comparing <see cref="TimeProvider.System"/> and <see cref="HighResolutionTimeProvider.Instance"/>.
/// Each benchmark requests 1 ms, as a delay, a due time or a period, and measures how long it actually takes.
/// </summary>
[Config(typeof(Config))]
[Outliers(OutlierMode.DontRemove)]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet uses [GlobalCleanup] methods for disposal.")]
public class TimerBenchmarks
{
    /// <summary>
    /// Adds the median, 95th percentile and maximum columns, and reports times in milliseconds.
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by BenchmarkDotNet via reflection.")]
    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Max);
            WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond));
        }
    }

    private static readonly TimeSpan s_desiredDuration = TimeSpan.FromMilliseconds(1);

    private PeriodicTimer? _periodicTimer;

    private ITimer? _timer;
    private SemaphoreSlim? _timerTickedSemaphore;

    /// <summary>
    /// The <see cref="Invicta.NamedTimeProvider"/>s to benchmark.
    /// </summary>
    public static IEnumerable<NamedTimeProvider> NamedTimeProviders
    {
        get
        {
            if (!HighResolutionTimeProvider.IsSupported)
            {
                throw new PlatformNotSupportedException(
                    $"{nameof(HighResolutionTimeProvider)} is not supported on this platform.");
            }

            return [
                new("System", TimeProvider.System),
                new("High Resolution", HighResolutionTimeProvider.Instance),
            ];
        }
    }

    /// <summary>
    /// The <see cref="Invicta.NamedTimeProvider"/> currently under test.
    /// </summary>
    [ParamsSource(nameof(NamedTimeProviders))]
    public NamedTimeProvider NamedTimeProvider { get; set; } = null!;

    /// <summary>
    /// Benchmarks the actual delay produced by <see cref="Task.Delay(TimeSpan, TimeProvider)"/>.
    /// </summary>
    [Benchmark]
    public Task TaskDelay()
    {
        return Task.Delay(s_desiredDuration, NamedTimeProvider.Provider);
    }

    /// <summary>
    /// Sets up the <see cref="PeriodicTimer"/> for <see cref="PeriodicTimerWaitForNextTickAsync"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(PeriodicTimerWaitForNextTickAsync))]
    public void PeriodicTimerWaitForNextTickAsyncSetup()
    {
        _periodicTimer = new(s_desiredDuration, NamedTimeProvider.Provider);
    }

    /// <summary>
    /// Benchmarks the actual interval between ticks produced by
    /// <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>.
    /// </summary>
    [Benchmark]
    public ValueTask<bool> PeriodicTimerWaitForNextTickAsync()
    {
        return _periodicTimer!.WaitForNextTickAsync();
    }

    /// <summary>
    /// Cleans up the <see cref="PeriodicTimer"/> for <see cref="PeriodicTimerWaitForNextTickAsync"/>.
    /// </summary>
    [GlobalCleanup(Target = nameof(PeriodicTimerWaitForNextTickAsync))]
    public void PeriodicTimerWaitForNextTickAsyncCleanup()
    {
        _periodicTimer?.Dispose();
    }

    /// <summary>
    /// Benchmarks the actual delay before a one-shot timer from
    /// <see cref="TimeProvider.CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)"/> invokes its callback.
    /// </summary>
    [Benchmark]
    public async Task TimeProviderCreateTimerOneShot()
    {
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ITimer timer = NamedTimeProvider.Provider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            fired,
            s_desiredDuration,
            Timeout.InfiniteTimeSpan);

        await fired.Task;
    }

    /// <summary>
    /// Sets up the <see cref="ITimer"/> for <see cref="TimeProviderCreateTimerPeriodic"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(TimeProviderCreateTimerPeriodic))]
    public void TimeProviderCreateTimerPeriodicSetup()
    {
        // The timer invokes OnTick for its entire lifetime, from this setup method
        // until cleanup, not only while TimeProviderCreateTimerPeriodic is being
        // measured. Between iterations, and especially between stages, BenchmarkDotNet
        // does work of its own, such as JIT compilation, overhead measurement and
        // garbage collection, which can take longer than a tick. If every tick
        // released the semaphore, the ticks during that work would accumulate as
        // permits, and the first invocations of the next iteration would complete
        // immediately, one for each accumulated tick, skewing the measurement.
        //
        // Releasing only when no permit is available discards the surplus ticks,
        // so at most one invocation completes immediately after each pause. The
        // long pauses fall mainly between stages, so that invocation is almost
        // always in a jitting, pilot or warmup iteration rather than a measured
        // one anyway.
        static void OnTick(object? state)
        {
            SemaphoreSlim semaphore = (SemaphoreSlim)state!;

            if (semaphore.CurrentCount == 0)
            {
                semaphore.Release();
            }
        }

        _timerTickedSemaphore = new SemaphoreSlim(0);
        _timer = NamedTimeProvider.Provider.CreateTimer(
            OnTick,
            _timerTickedSemaphore,
            s_desiredDuration,
            s_desiredDuration);
    }

    /// <summary>
    /// Benchmarks the actual interval between the callbacks of a periodic timer from
    /// <see cref="TimeProvider.CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)"/>.
    /// </summary>
    [Benchmark]
    public Task TimeProviderCreateTimerPeriodic()
    {
        return _timerTickedSemaphore!.WaitAsync();
    }

    /// <summary>
    /// Cleans up the <see cref="ITimer"/> for <see cref="TimeProviderCreateTimerPeriodic"/>.
    /// </summary>
    [GlobalCleanup(Target = nameof(TimeProviderCreateTimerPeriodic))]
    public async Task TimeProviderCreateTimerPeriodicCleanup()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        _timerTickedSemaphore?.Dispose();
    }
}
