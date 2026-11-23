using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeModifierHookBackend
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int type,
        ModifierHookProcedure callback, nint module, uint thread);
}
