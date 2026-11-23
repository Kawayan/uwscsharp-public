using System.Globalization;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

// コマンドIDから識別した項目を、メニューの階層パスで再生します。
internal sealed record RecordedMenuClick(
    RecordedWindow Window, string Path, uint CommandId, nint Menu, int Index)
{
    internal static RecordedMenuClick? CaptureAtPoint(nint window, Native.Point point)
    {
        nint root = Native.GetAncestor(window, 2);
        if (root == 0 || root != window)
        {
            return null;
        }

        nint menu = NativeMenuControl.GetMenu(root);
        int index = menu == 0 ? -1 : NativeMenuControl.MenuItemFromPoint(root, menu, point);
        if (index < 0 || NativeMenuControl.GetSubMenu(menu, index) != 0)
        {
            return null;
        }

        uint command = NativeMenuControl.GetMenuItemID(menu, index);
        return command == uint.MaxValue ? null : Capture(root, command);
    }

    internal static RecordedMenuClick? Capture(nint root, uint command)
    {
        if (RecordedWindow.Capture(root) is not { } window
            || window.Handle != root
            || NativeMenuControl.FindRecordingItem(root, command) is not { } item)
        {
            return null;
        }

        return new(window, item.Path, item.Id, item.Menu, item.Index);
    }

    internal string Command()
    {
        string window = Window.Identifier();
        string path = RecordedWindow.Literal(Path);
        int flags = (int)(ClickItemFlags.Menu | ClickItemFlags.Api);
        string mode = flags.ToString(CultureInfo.InvariantCulture);
        return $"{BuiltinFunctionNames.ClkItem}({window},{path},{mode},1,1)";
    }
}
