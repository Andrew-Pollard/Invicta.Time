// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using BenchmarkDotNet.Running;

namespace Invicta;

/// <summary>Runs the benchmarks.</summary>
internal static class Program
{
    /// <summary>Runs the benchmarks selected on the command line, or prompts for them if none are given.</summary>
    /// <param name="args">BenchmarkDotNet's command-line arguments, such as <c>--filter</c>.</param>
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
