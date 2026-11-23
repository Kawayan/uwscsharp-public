using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class SensorApartment
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
