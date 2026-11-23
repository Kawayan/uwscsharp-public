using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class SensorPropertyValueNativeApi
{
    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToBooleanWithDefault(
        nint propertyValue, int defaultValue);

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern nint PropVariantToStringWithDefault(
        nint propertyValue, nint defaultValue);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(nint propertyValue);
}
