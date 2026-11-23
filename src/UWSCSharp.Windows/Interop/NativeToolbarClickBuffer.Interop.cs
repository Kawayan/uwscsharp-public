using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeToolbarClickBuffer
{
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageA(nint window, uint message,
        nint parameter, nint pointer);
}
