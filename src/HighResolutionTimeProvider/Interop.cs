// © 2026 Andrew Pollard. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HighResolutionTime;

internal static partial class Interop
{
    // Win32 constants are PascalCased per .editorconfig; the SDK names are noted alongside.
    internal const uint CreateWaitableTimerHighResolution = 0x00000002; // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
    internal const uint TimerModifyState = 0x0002; // TIMER_MODIFY_STATE
    internal const uint Synchronize = 0x00100000; // SYNCHRONIZE
    internal const uint Infinite = 0xFFFFFFFF; // INFINITE
    internal const uint WaitFailed = 0xFFFFFFFF; // WAIT_FAILED

    private const string Kernel32 = "kernel32.dll";

    [LibraryImport(Kernel32, EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    internal static partial SafeWaitHandle CreateWaitableTimerEx(
        nint lpTimerAttributes,
        nint lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    // lpDueTime: negative values are relative time in 100 ns units.
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);
}
