using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeScrollControl
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetScrollInfo(nint target, int bar, ref ScrollInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetScrollInfo(nint target, int bar,
        ref ScrollInfo info, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
