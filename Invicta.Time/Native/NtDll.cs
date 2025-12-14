using System.Runtime.InteropServices;

namespace Invicta.Time.Native;

internal static partial class NtDll
{
    private const string LibraryName = "NtDll";

    [LibraryImport(LibraryName)]
    public static partial uint NtQueryTimerResolution(out int MinimumResolution, out int MaximumResolution, out int ActualResolution);
}
