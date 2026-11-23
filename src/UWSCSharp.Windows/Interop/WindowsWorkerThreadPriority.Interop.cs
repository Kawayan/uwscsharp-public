using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

internal sealed partial class WindowsWorkerThreadPriority
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint desiredAccess,
        bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(SafeWaitHandle thread,
        int priority);
}
