using System.Runtime.InteropServices;

namespace Invicta.Time.Native;

internal static partial class Kernel32
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint INFINITE = 0xFFFFFFFF;

    public const uint WAIT_ABANDONED = 0x00000080;
    public const uint WAIT_OBJECT_0 = 0x00000000;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;

    private const string LibraryName = "Kernel32";

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint CreateWaitableTimerExW(nint lpTimeAttributes, nint lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetWaitableTimer(
        nint hTimer,
        in long lpDueTime,
        int lPeriod,
        delegate* unmanaged[Stdcall]<nint, uint, uint, void> pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume
    );

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetWaitableTimerEx(
        nint hTimer,
        in long lpDueTime,
        int lPeriod,
        delegate* unmanaged[Stdcall]<nint, uint, uint, void> pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        nint WakeContext,
        uint TolerableDelay
    );

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial uint WaitForMultipleObjects(
        uint nCount,
        [In, MarshalAs(UnmanagedType.LPArray)] nint[] lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds
    );

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelWaitableTimer(nint hTimer);

    [LibraryImport(LibraryName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}
