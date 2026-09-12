// © 2026 Andrew Pollard. All rights reserved.

using BenchmarkDotNet.Running;

namespace Invicta;

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
