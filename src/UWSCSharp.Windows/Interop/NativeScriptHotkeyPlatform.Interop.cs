using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeScriptHotkeyPlatform
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id,
        uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
