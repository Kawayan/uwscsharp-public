using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeComRuntime
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string name, out Guid id);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid id, nint reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object value);
}
