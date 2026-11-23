using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class ComSafeArray
{
    [DllImport("oleaut32.dll")]
    private static extern uint SafeArrayGetDim(nint array);

    [DllImport("oleaut32.dll")]
    private static extern uint SafeArrayGetElemsize(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetVartype(nint array, out ushort type);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint array, uint dimension, out int bound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(nint array, [In] int[] indices, nint value);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayLock(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnlock(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPtrOfIndex(nint array,
        [In] int[] indices, out nint value);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPutElement(nint array, [In] int[] indices, nint value);

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreate(ushort type, uint rank, [In] Bound[] bounds);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int VariantChangeTypeEx(nint destination, nint source,
        uint locale, ushort flags, ushort type);
}
