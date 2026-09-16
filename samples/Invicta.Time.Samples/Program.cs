// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

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
    // The longest period, in milliseconds, that PeriodicTimer supports.
    private const double MaxIntervalMs = 4294967294;

    /// <summary>Measures both providers and writes the samples to CSV.</summary>
    /// <param name="args">Optional sample count, interval in milliseconds, and output path.</param>
    /// <returns>Zero on success, or 1 when the arguments are not valid.</returns>
    private static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out int sampleCount, out TimeSpan interval, out string path))
        {
            PrintUsage();
            return 1;
        }

        (string Name, TimeProvider Provider)[] providers =
        [
            ("System", TimeProvider.System),
            ("HighResolution", HighResolutionTimeProvider.Instance),
        ];

        await using StreamWriter writer = new(path);
        await writer.WriteLineAsync("provider,scenario,sample,milliseconds");

        foreach ((string name, TimeProvider provider) in providers)
        {
            await WriteSamplesAsync(
                writer, name, "Task.Delay", await MeasureDelaysAsync(provider, interval, sampleCount));
            await WriteSamplesAsync(
                writer, name, "PeriodicTimer", await MeasureTicksAsync(provider, interval, sampleCount));
        }

        Console.WriteLine($"Wrote {sampleCount * providers.Length * 2} samples to {Path.GetFullPath(path)}");

        return 0;
    }

    /// <summary>Reads the optional arguments, using defaults for any that are not given.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="sampleCount">The number of samples to take per scenario; 500 by default.</param>
    /// <param name="interval">The delay and timer period to measure; 1 ms by default.</param>
    /// <param name="path">The CSV file to write; latency.csv by default.</param>
    /// <returns>
    /// <see langword="true"/> if every argument given is valid; otherwise, <see langword="false"/>.
    /// </returns>
    private static bool TryParseArguments(string[] args, out int sampleCount, out TimeSpan interval, out string path)
    {
        sampleCount = 500;
        double intervalMs = 1;
        path = args.Length > 2 ? args[2] : "latency.csv";

        bool isSampleCountValid = args.Length < 1
            || (int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out sampleCount)
                && sampleCount > 0);

        bool isIntervalValid = args.Length < 2
            || (double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out intervalMs)
                && intervalMs is > 0 and <= MaxIntervalMs);

        interval = TimeSpan.FromMilliseconds(isIntervalValid ? intervalMs : 1);

        return args.Length <= 3 && isSampleCountValid && isIntervalValid;
    }

    /// <summary>Prints how to run the sample.</summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: Invicta.Time.Samples [sample count] [interval in milliseconds] [output path]");
    }

    /// <summary>Measures how long each <see cref="Task.Delay(TimeSpan, TimeProvider)"/> actually takes.</summary>
    /// <param name="provider">The provider to delay with.</param>
    /// <param name="interval">The delay to request.</param>
    /// <param name="count">The number of delays to measure.</param>
    /// <returns>One elapsed time in milliseconds per sample.</returns>
    private static async Task<double[]> MeasureDelaysAsync(TimeProvider provider, TimeSpan interval, int count)
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
    /// <param name="provider">The provider to create the timer with.</param>
    /// <param name="interval">The timer's period.</param>
    /// <param name="count">The number of intervals to measure.</param>
    /// <returns>One interval in milliseconds per sample.</returns>
    private static async Task<double[]> MeasureTicksAsync(TimeProvider provider, TimeSpan interval, int count)
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
    /// <param name="writer">The CSV file to write to.</param>
    /// <param name="provider">The provider name for the first column.</param>
    /// <param name="scenario">The scenario name for the second column.</param>
    /// <param name="samples">The samples, in milliseconds.</param>
    /// <returns>A task that completes once every row is written.</returns>
    private static async Task WriteSamplesAsync(StreamWriter writer, string provider, string scenario, double[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            await writer.WriteLineAsync(
                string.Create(CultureInfo.InvariantCulture, $"{provider},{scenario},{i},{samples[i]:F4}"));
        }
    }
}
