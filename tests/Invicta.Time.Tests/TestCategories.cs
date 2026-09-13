// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

namespace Invicta;

/// <summary>The names of the NUnit categories that tests are grouped into.</summary>
internal static class TestCategories
{
    /// <summary>
    /// Tests that expect callbacks within a time limit, or at a minimum rate, and so can fail on a heavily loaded
    /// machine. Exclude them with <c>dotnet test --filter TestCategory!=Timing</c>.
    /// </summary>
    public const string Timing = nameof(Timing);
}
