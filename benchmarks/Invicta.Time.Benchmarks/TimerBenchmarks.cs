// © 2026 Andrew Pollard. All rights reserved.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;
using Perfolizer.Mathematics.OutlierDetection;

namespace Invicta;

/// <summary>
/// Timer latency for <see cref="TimeProvider.System"/> against <see cref="HighResolutionTimeProvider"/>.
/// Each benchmark measures one wait, so the reported time is the delay itself rather than call overhead.
/// </summary>
[Config(typeof(LatencyConfig))]
[Outliers(OutlierMode.DontRemove)]
public class TimerBenchmarks
{
    private PeriodicTimer? _periodic;

    public static IEnumerable<NamedTimeProvider> Providers =>
    [
        new("System", TimeProvider.System),
        new("HighResolution", HighResolutionTimeProvider.Instance),
    ];

    [ParamsSource(nameof(Providers))]
    public NamedTimeProvider Clock { get; set; } = null!;

    [GlobalSetup]
    public void CreatePeriodicTimer() => _periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(1), Clock.Provider);

    [GlobalCleanup]
    public void DisposePeriodicTimer() => _periodic?.Dispose();

    /// <summary>A one millisecond <see cref="Task.Delay(TimeSpan, TimeProvider)"/>.</summary>
    [Benchmark]
    public Task TaskDelay() => Task.Delay(TimeSpan.FromMilliseconds(1), Clock.Provider);

    /// <summary>One tick of a one millisecond <see cref="PeriodicTimer"/>.</summary>
    [Benchmark]
    public ValueTask<bool> PeriodicTimerTick() => _periodic!.WaitForNextTickAsync();

    /// <summary>Creating a one-shot <see cref="ITimer"/> and waiting for its callback.</summary>
    [Benchmark]
    public async Task OneShotTimer()
    {
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ITimer timer = Clock.Provider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            fired,
            TimeSpan.FromMilliseconds(1),
            Timeout.InfiniteTimeSpan);

        await fired.Task;
    }
}

/// <summary>A <see cref="TimeProvider"/> with a short name, so it reads well in the results table.</summary>
public sealed class NamedTimeProvider(string name, TimeProvider provider)
{
    public TimeProvider Provider { get; } = provider;

    public override string ToString() => name;
}

/// <summary>
/// Reports the median, 95th percentile and maximum in milliseconds, and keeps outliers, because the tail of the
/// distribution is the point of these measurements.
/// </summary>
public sealed class LatencyConfig : ManualConfig
{
    public LatencyConfig()
    {
        AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Max);
        WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond));
    }
}
