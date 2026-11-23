using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class ComErrorInfo
{
    [DllImport("oleaut32.dll")]
    private static extern int GetErrorInfo(uint reserved, out IErrorInfo? info);
}
