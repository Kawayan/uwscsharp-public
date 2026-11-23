using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

public sealed partial class BasicRecorder
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int id,
        HookCallback callback, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook,
        int code, nint message, nint data);
}
