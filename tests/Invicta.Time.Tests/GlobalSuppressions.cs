// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
    Justification = "HighResolutionTimeProvider is Windows-only, so these tests only run on Windows.")]

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "Tests do not have a synchronization context.")]

[assembly: SuppressMessage("Security", "CA5394:Do not use insecure randomness",
    Justification = "A seeded System.Random is deliberate, for reproducible test data.")]
