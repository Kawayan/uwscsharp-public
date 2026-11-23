using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class FileDropTarget
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    private static extern nint ChildWindowFromPointEx(nint parent, Native.Point point, uint flags);
}
