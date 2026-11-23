using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

public static partial class WindowsScriptWindowLayout
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref Placement placement);
}
