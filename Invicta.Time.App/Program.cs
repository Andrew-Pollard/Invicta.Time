using System.Diagnostics;

namespace Invicta.Time.App;

internal sealed class Program
{
    public static async Task Main(string[] args)
    {
        int iterations = int.Parse(args[0]);
        TimeSpan interval = TimeSpan.FromMilliseconds(double.Parse(args[1]));

        PeriodicTimer timer = new(interval, WaitableTimerHighResolutionTimeProvider.Instance);

        List<TimeSpan> waits = new(iterations);

        Stopwatch stopwatch = new();
        for (int i = 0; i < iterations; i++)
        {
            stopwatch.Restart();
            await timer.WaitForNextTickAsync();
            waits.Add(stopwatch.Elapsed);
        }

        File.WriteAllLines("out.csv", waits.Select(t => t.TotalMilliseconds.ToString()));

        Console.WriteLine("Complete");
    }

    //public static void Main(string[] args)
    //{
    //    int iterations = int.Parse(args[0]);
    //    TimeSpan interval = TimeSpan.FromMilliseconds(double.Parse(args[1]));

    //    List<TimeSpan> waits = new(iterations);

    //    Stopwatch stopwatch = new();
    //    for (int i = 0; i < iterations; i++)
    //    {
    //        stopwatch.Restart();
    //        HighResolutionTimer.Sleep(interval);
    //        waits.Add(stopwatch.Elapsed);
    //    }

    //    File.WriteAllLines("out.csv", waits.Select(t => t.TotalMilliseconds.ToString()));

    //    Console.WriteLine("Complete");
    //}
}
