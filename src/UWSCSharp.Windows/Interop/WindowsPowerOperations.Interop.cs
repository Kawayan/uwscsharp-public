using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

internal static partial class WindowsPowerOperations
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Luid(uint LowPart, int HighPart);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TokenPrivileges(uint Count, Luid Luid, uint Attributes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name,
        out Luid luid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        bool disableAll, in TokenPrivileges newState, uint bufferLength,
        out TokenPrivileges previousState, out uint returnLength);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        bool disableAll, in TokenPrivileges newState, uint bufferLength,
        nint previousState, nint returnLength);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);
}
