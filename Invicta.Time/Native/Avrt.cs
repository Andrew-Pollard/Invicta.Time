using System.Runtime.InteropServices;

namespace Invicta.Time.Native;

internal static partial class Avrt
{
    private const string LibraryName = "Avrt";

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint AvSetMmThreadCharacteristicsW([MarshalAs(UnmanagedType.LPWStr)] string TaskName, ref uint TaskIndex);

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvSetMmThreadPriority(nint AvrtHandle, AVRT_PRIORITY Priority);
}
