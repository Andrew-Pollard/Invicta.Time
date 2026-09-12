// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Maintainability", "CA1515:Consider making public types internal",
    Justification = "BenchmarkDotNet requires benchmark classes to be public.")]

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Console apps do not have a synchronization context.")]
