using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class SensorManagerFactory
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid classId,
        nint outer, uint context, in Guid interfaceId, out nint manager);
}
