// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using BenchmarkDotNet.Running;

namespace Invicta;

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
