using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class WindowMovementMonitor
{
    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint minimum, uint maximum,
        nint module, WinEventCallback callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
}
