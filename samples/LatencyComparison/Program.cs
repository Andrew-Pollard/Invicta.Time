// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics;
using Invicta;

// Compares how long Task.Delay(1 ms) and a 1 ms PeriodicTimer actually take with each provider.

const int Samples = 200;

await Measure("TimeProvider.System", TimeProvider.System);
await Measure("HighResolutionTimeProvider.Instance", HighResolutionTimeProvider.Instance);

static async Task Measure(string name, TimeProvider provider)
{
    Console.WriteLine(name);

    await Task.Delay(TimeSpan.FromMilliseconds(1), provider); // warm-up
    double[] delays = new double[Samples];
    for (int i = 0; i < Samples; i++)
    {
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(1), provider);
        delays[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    Print("  Task.Delay(1 ms)        ", delays);

    using PeriodicTimer periodic = new(TimeSpan.FromMilliseconds(1), provider);
    double[] intervals = new double[Samples];
    await periodic.WaitForNextTickAsync();
    long last = Stopwatch.GetTimestamp();
    for (int i = 0; i < Samples; i++)
    {
        await periodic.WaitForNextTickAsync();
        long now = Stopwatch.GetTimestamp();
        intervals[i] = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
        last = now;
    }

    Print("  PeriodicTimer(1 ms) tick", intervals);
    Console.WriteLine();
}

static void Print(string label, double[] values)
{
    Array.Sort(values);
    Console.WriteLine(
        $"{label}  min {values[0],7:F3}  p50 {values[values.Length / 2],7:F3}  " +
        $"p99 {values[(int)(values.Length * 0.99)],7:F3}  max {values[values.Length - 1],7:F3}  " +
        $"mean {values.Average(),7:F3}  (ms)");
}
