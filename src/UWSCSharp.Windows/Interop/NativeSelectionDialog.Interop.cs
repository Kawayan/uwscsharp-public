using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    private sealed partial class SelectionWindow
    {
        [DllImport("user32.dll")]
        private static extern nint GetSysColorBrush(int color);

        [DllImport("user32.dll")]
        private static extern int FillRect(nint dc, in Native.Rect rect,
            nint brush);

        [DllImport("user32.dll")]
        private static extern bool DrawFocusRect(nint dc, in Native.Rect rect);
    }
}
