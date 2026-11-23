using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeKeyStateBackend
{
    [DllImport("imm32.dll")]
    private static extern nint ImmGetDefaultIMEWnd(nint window);
}
