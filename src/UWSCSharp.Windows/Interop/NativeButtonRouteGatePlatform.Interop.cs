using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeButtonRouteGatePlatform
{
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint thread,
        Native.EnumCallback callback, nint data);
}
