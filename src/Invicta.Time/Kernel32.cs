// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Invicta;

/// <summary>Declares the kernel32.dll functions and constants that the library calls through P/Invoke.</summary>
[SuppressMessage("Style", "IDE1006:Naming Styles",
    Justification = "Names follow the Win32 headers, per the .NET native interoperability best practices.")]
internal static partial class Kernel32
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    public const uint TIMER_MODIFY_STATE = 0x0002;
    public const uint SYNCHRONIZE = 0x00100000;

    /// <summary>Creates or opens a waitable timer object.</summary>
    /// <param name="lpTimerAttributes">
    /// An optional <c>SECURITY_ATTRIBUTES</c> pointer, always zero here, so that structure is not declared.
    /// </param>
    /// <param name="lpTimerName">A name for the timer, or null for an unnamed one.</param>
    /// <param name="dwFlags">
    /// <see cref="CREATE_WAITABLE_TIMER_HIGH_RESOLUTION"/>, or zero for a timer at the default resolution.
    /// </param>
    /// <param name="dwDesiredAccess">The access rights requested for the handle.</param>
    /// <returns>A handle to the timer, or an invalid handle on failure.</returns>
    /// <seealso href="https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw"/>
    [LibraryImport(nameof(Kernel32), SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeWaitHandle CreateWaitableTimerExW(
        nint lpTimerAttributes,
        string? lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    /// <summary>Activates the timer, either as a one-shot or as a periodic timer.</summary>
    /// <param name="hTimer">The timer to activate.</param>
    /// <param name="lpDueTime">
    /// When the timer is first signaled. Negative values are a relative time in 100 ns units; positive values
    /// are an absolute file time.
    /// </param>
    /// <param name="lPeriod">The period in milliseconds, or zero for a timer that signals once.</param>
    /// <param name="pfnCompletionRoutine">
    /// An optional <c>PTIMERAPCROUTINE</c> to queue as an APC when the timer signals, always zero here.
    /// </param>
    /// <param name="lpArgToCompletionRoutine">The argument passed to that APC, always zero here.</param>
    /// <param name="fResume">Nonzero to wake a suspended system when the timer signals, zero here.</param>
    /// <returns>A Win32 <c>BOOL</c>, which is a 4-byte int that is zero on failure.</returns>
    /// <seealso href="https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-setwaitabletimer"/>
    [LibraryImport(nameof(Kernel32), SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        nint pfnCompletionRoutine,
        nint lpArgToCompletionRoutine,
        int fResume);
}
