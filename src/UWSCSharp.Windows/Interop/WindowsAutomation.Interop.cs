using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        [DllImport("user32.dll")]
        private static extern nint GetSystemMenu(nint window, bool revert);

        [DllImport("user32.dll", EntryPoint = "SetFocus")]
        private static extern nint ShortcutSetFocus(nint target);

        [DllImport("user32.dll", EntryPoint = "SetActiveWindow")]
        private static extern nint ShortcutSetActiveWindow(nint target);
    }
}
