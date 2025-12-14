using System.Runtime.InteropServices;

namespace Invicta.Time.Native;

internal static partial class Winmm
{
    public const uint TIMERR_NOERROR = 0;
    public const uint TIMERR_NOCANDO = 97;

    private const string LibraryName = "Winmm";

    [LibraryImport(LibraryName)]
    public static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport(LibraryName)]
    public static partial uint timeEndPeriod(uint uPeriod);
}
