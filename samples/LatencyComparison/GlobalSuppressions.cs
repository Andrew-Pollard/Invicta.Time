// © 2026 Andrew Pollard. All rights reserved.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
    Justification = "HighResolutionTimeProvider is Windows-only, so this sample only runs on Windows.")]

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Console apps do not have a synchronization context.")]
