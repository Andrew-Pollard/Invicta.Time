// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

namespace Invicta;

/// <summary>
/// Pairs a <see cref="TimeProvider"/> with the name that the benchmark output shows for it.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Provider">The time provider.</param>
public sealed record NamedTimeProvider(string Name, TimeProvider Provider)
{
    /// <summary>
    /// Returns <see cref="Name"/>, which BenchmarkDotNet shows in the benchmark output.
    /// </summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;
}
