using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal partial class ProviderCleanupOperations
{
    [DllImport("oleaut32.dll", EntryPoint = "VariantClear")]
    private static extern int VariantClear(nint value);
}
