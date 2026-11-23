using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeAccessibleActivationBackend
{
    [DllImport("user32.dll", EntryPoint = "AnyPopup")]
    private static extern bool NativeAnyPopup();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action,
        uint parameter, ref uint value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SetSystemParametersInfoW(uint action,
        uint parameter, nint value, uint flags);
}
