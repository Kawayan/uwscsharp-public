using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial record OperatingSystemInformation
{
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RtlGetVersion(ref VersionInfo version);
}
