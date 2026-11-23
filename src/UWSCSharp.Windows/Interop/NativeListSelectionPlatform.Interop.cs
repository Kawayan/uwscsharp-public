using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal partial class NativeListSelectionPlatform
{
    [DllImport("user32.dll")]
    private static extern uint GetClassLongW(nint window, int index);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(nint process, uint priority);

    [DllImport("user32.dll")]
    private static extern bool LockWindowUpdate(nint window);
}
