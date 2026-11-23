using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeMenuControl
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuInfo(nint menu, ref MenuInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(nint menu, uint item,
        [Out] char[] text, int capacity, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetSubMenu(nint menu, int position);

    [DllImport("user32.dll")]
    internal static extern uint GetMenuItemID(nint menu, int position);

    [DllImport("user32.dll")]
    internal static extern nint GetMenu(nint root);

    [DllImport("user32.dll")]
    internal static extern int MenuItemFromPoint(
        nint root, nint menu, Native.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMenuItemCount(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemInfoW(nint menu, uint item,
        [MarshalAs(UnmanagedType.Bool)] bool position, ref MenuItemInfo info);
}
