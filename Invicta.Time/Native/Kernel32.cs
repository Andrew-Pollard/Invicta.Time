using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Invicta.Time.Native;

internal static partial class Kernel32
{
    public const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    public const uint TIMER_ALL_ACCESS = 0x1F0003;

    private const string LibraryName = "Kernel32";

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial SafeWaitHandle CreateWaitableTimerExW(
        nint lpTimeAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess
    );

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetWaitableTimerEx(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        delegate* unmanaged[Stdcall]<nint, uint, uint, void> pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        nint WakeContext,
        uint TolerableDelay
    );

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelWaitableTimer(SafeWaitHandle hTimer);
}
