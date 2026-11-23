using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

internal static partial class SchedulerPipeIdentity
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
}
