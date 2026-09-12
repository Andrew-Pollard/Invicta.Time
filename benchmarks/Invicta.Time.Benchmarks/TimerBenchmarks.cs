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
/// </summary>
[Config(typeof(Config))]
[Outliers(OutlierMode.DontRemove)]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet uses PeriodicTimerWaitForNextTickAsyncCleanup for disposal.")]
public class TimerBenchmarks
{
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

    private PeriodicTimer? _periodicTimer;

    /// <summary>
    /// The desired duration of the delay for each benchmark.
    /// </summary>
    public static TimeSpan DesiredDuration { get; } = TimeSpan.FromMilliseconds(1);

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
    /// Benchmarks the actual delay produced by <see cref="Task.Delay(TimeSpan, TimeProvider)"/>
    /// when invoked with a delay of <see cref="DesiredDuration"/>.
    /// </summary>
    [Benchmark]
    public Task TaskDelay()
    {
        return Task.Delay(DesiredDuration, NamedTimeProvider.Provider);
    }

    /// <summary>
    /// Sets up the <see cref="PeriodicTimer"/> for <see cref="PeriodicTimerWaitForNextTickAsync"/>.
    /// </summary>
    [GlobalSetup(Target = nameof(PeriodicTimerWaitForNextTickAsync))]
    public void PeriodicTimerWaitForNextTickAsyncSetup()
    {
        _periodicTimer = new(DesiredDuration, NamedTimeProvider.Provider);
    }

    /// <summary>
    /// Benchmarks the actual interval between ticks produced by
    /// <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/> when
    /// invoked with a period of <see cref="DesiredDuration"/>.
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
    /// Benchmarks the actual delay produced by
    /// <see cref="TimeProvider.CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)"/>
    /// when invoked with a due time of <see cref="DesiredDuration"/>.
    /// </summary>
    [Benchmark]
    public async Task TimeProviderCreateTimerOneShot()
    {
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ITimer timer = NamedTimeProvider.Provider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            fired,
            DesiredDuration,
            Timeout.InfiniteTimeSpan);

        await fired.Task;
    }
}
