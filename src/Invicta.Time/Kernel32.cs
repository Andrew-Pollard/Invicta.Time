// © 2026 Andrew Pollard. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Invicta;

// Names and types follow the Win32 headers rather than .editorconfig's .NET naming rules, per
// https://learn.microsoft.com/dotnet/standard/native-interop/best-practices.
#pragma warning disable IDE1006 // Naming Styles

/// <summary>P/Invoke declarations for kernel32.dll.</summary>
internal static unsafe partial class Kernel32
{
    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    internal const uint TIMER_MODIFY_STATE = 0x0002;
    internal const uint SYNCHRONIZE = 0x00100000;
    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint WAIT_FAILED = 0xFFFFFFFF;

    // lpTimerAttributes is only ever null here, so SECURITY_ATTRIBUTES isn't declared.
    [LibraryImport(nameof(Kernel32), SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle CreateWaitableTimerExW(
        void* lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    // A negative lpDueTime is a relative time in 100 ns units. BOOL is a 4-byte int that is zero on failure.
    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    internal static partial int SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        delegate* unmanaged<void*, uint, uint, void> pfnCompletionRoutine,
        void* lpArgToCompletionRoutine,
        int fResume);

    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeWaitHandle hHandle, uint dwMilliseconds);
}
