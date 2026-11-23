using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeTouchInputBackend
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateSyntheticPointerDevice(
        uint type, uint maximum, uint feedback);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(
        DeviceHandle device, [In] PointerTypeInfo[] contacts, uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(nint device);
}
