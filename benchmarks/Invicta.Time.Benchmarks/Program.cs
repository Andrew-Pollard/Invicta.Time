// © 2026 Andrew Pollard. All rights reserved.

using BenchmarkDotNet.Running;
using Invicta;

BenchmarkSwitcher.FromAssembly(typeof(TimerBenchmarks).Assembly).Run(args);
