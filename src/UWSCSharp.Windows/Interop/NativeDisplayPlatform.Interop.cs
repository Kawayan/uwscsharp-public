using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDisplayPlatform
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string deviceName,
        uint number, ref DisplayDevice device, uint flags);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);
}
