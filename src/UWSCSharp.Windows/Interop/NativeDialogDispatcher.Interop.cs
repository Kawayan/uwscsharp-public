using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogDispatcher
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool IsDialogMessageW(nint dialog, ref Native.Message message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint window, int index, int value);
}
