using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class RawInputHookMonitor
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(
        [Out] RawDevice[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command,
        out RawPacket packet, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint window, nuint id);
}
