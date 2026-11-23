using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class CursorReader
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo info);
}
