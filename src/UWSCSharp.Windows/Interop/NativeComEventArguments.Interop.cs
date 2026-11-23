using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeComEventArguments
{
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int VariantCopy(nint destination, nint source);
}
