using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Invicta.Time.Native;

internal static partial class Kernel32
{
    public const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    [SupportedOSPlatform("windows10.0.17134.0")]
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    public const uint TIMER_ALL_ACCESS = 0x1F0003;

    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    public static partial SafeWaitHandle CreateWaitableTimerExW(
        nint lpTimeAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess
    );

    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimerEx(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        nint WakeContext,
        uint TolerableDelay
    );

    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelWaitableTimer(SafeWaitHandle hTimer);
}
