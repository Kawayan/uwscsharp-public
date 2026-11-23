using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class Native
{
    // MSGに対応します。Privateは旧定義との互換のために残す予備領域です。
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out Message message, nint window,
        uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out Message message, nint window,
        uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessageW(uint thread,
        uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint count,
        nint[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(nint window, nuint id,
        uint interval, nint callback);
}
