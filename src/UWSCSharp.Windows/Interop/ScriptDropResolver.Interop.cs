using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class ScriptDropResolver
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
