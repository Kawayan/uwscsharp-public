using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class ComSession
{
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
