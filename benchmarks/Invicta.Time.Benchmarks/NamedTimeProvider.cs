// © 2026 Andrew Pollard. All rights reserved.

namespace Invicta;

/// <summary>
/// A named <see cref="TimeProvider"/>. The name will be shown in the benchmark
/// output.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Provider">The time provider.</param>
public sealed record NamedTimeProvider(string Name, TimeProvider Provider)
{
    public override string ToString() => Name;
}
