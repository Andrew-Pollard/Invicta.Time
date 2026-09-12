// © 2026 Andrew Pollard. All rights reserved.

using BenchmarkDotNet.Running;

namespace Invicta;

internal static class Program
{
    /// <summary>Runs the benchmarks; pass <c>--filter *</c> to select them all without prompting.</summary>
    /// <param name="args">BenchmarkDotNet command line arguments.</param>
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(TimerBenchmarks).Assembly).Run(args);
}
