using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class CommandOutputDecoder
{
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
