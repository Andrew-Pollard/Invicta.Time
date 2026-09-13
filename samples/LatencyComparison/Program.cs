// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using System.Globalization;

namespace Invicta;

/// <summary>
/// Writes per-sample timer latencies to CSV, for the distribution behind the numbers rather than the numbers
/// themselves: benchmarks/Invicta.Time.Benchmarks reports the aggregates, but its iteration averages hide the
/// behavior of individual calls.
/// </summary>
internal static class Program
{
    /// <summary>Measures both providers and writes the samples to CSV.</summary>
    /// <param name="args">Optional sample count, interval in milliseconds, and output path.</param>
    private static async Task Main(string[] args)
    {
        int sampleCount = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 500;
        double intervalMs = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1;
        string path = args.Length > 2 ? args[2] : "latency.csv";
        TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);

        (string Name, TimeProvider Provider)[] clocks =
        [
            ("System", TimeProvider.System),
            ("HighResolution", HighResolutionTimeProvider.Instance),
        ];

        await using StreamWriter writer = new(path);
        await writer.WriteLineAsync("provider,scenario,sample,milliseconds");

        foreach ((string name, TimeProvider provider) in clocks)
        {
            await WriteSamples(writer, name, "Task.Delay", await MeasureDelays(provider, interval, sampleCount));
            await WriteSamples(writer, name, "PeriodicTimer", await MeasureTicks(provider, interval, sampleCount));
        }

        Console.WriteLine($"Wrote {sampleCount * clocks.Length * 2} samples to {Path.GetFullPath(path)}");
    }

    /// <summary>Measures how long each <see cref="Task.Delay(TimeSpan, TimeProvider)"/> actually takes.</summary>
    /// <returns>One elapsed time in milliseconds per sample.</returns>
    private static async Task<double[]> MeasureDelays(TimeProvider provider, TimeSpan interval, int count)
    {
        // Warm up the provider and JIT the path.
        await Task.Delay(interval, provider);

        double[] samples = new double[count];
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(interval, provider);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        return samples;
    }

    /// <summary>Measures the gap between consecutive <see cref="PeriodicTimer"/> ticks.</summary>
    /// <returns>One interval in milliseconds per sample.</returns>
    private static async Task<double[]> MeasureTicks(TimeProvider provider, TimeSpan interval, int count)
    {
        double[] samples = new double[count];
        using PeriodicTimer timer = new(interval, provider);

        await timer.WaitForNextTickAsync();
        long last = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            await timer.WaitForNextTickAsync();
            long now = Stopwatch.GetTimestamp();

            samples[i] = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            last = now;
        }

        return samples;
    }

    /// <summary>Writes one CSV row per sample.</summary>
    private static async Task WriteSamples(StreamWriter writer, string provider, string scenario, double[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            await writer.WriteLineAsync(
                string.Create(CultureInfo.InvariantCulture, $"{provider},{scenario},{i},{samples[i]:F4}"));
        }
    }
}
