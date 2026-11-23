using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeButtonAccessibleApi
{
    [DllImport("oleaut32.dll", EntryPoint = "VariantClear")]
    internal static extern int Clear(ref NativeButtonVariant value);

    [DllImport("oleacc.dll", EntryPoint = "AccessibleObjectFromWindow")]
    internal static extern int FromWindow(nint window, uint objectId,
        ref Guid id, out nint result);

    [DllImport("oleacc.dll", EntryPoint = "AccessibleObjectFromPoint")]
    internal static extern int FromPoint(Native.Point point,
        out nint result, ref NativeButtonVariant child);
}
