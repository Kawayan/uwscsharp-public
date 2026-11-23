using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class SpeechRecognitionAutomationText
{
    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int VarBstrFromCy(long value,
        uint locale, uint flags, out nint result);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int VarBstrFromDate(double value,
        uint locale, uint flags, out nint result);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int VarBstrFromBool(short value,
        uint locale, uint flags, out nint result);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int VariantChangeTypeEx(
        [Out, MarshalAs(UnmanagedType.Struct)] out object destination,
        [In, MarshalAs(UnmanagedType.Struct)] ref object source,
        uint locale, ushort flags, ushort type);
}
