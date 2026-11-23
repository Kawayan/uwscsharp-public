using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UWSCSharp.Windows;

internal sealed partial class NativeComEventConnection
{
    [ComImport]
    [Guid("B196B283-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProvideClassInfo
    {
        [PreserveSig]
        int GetClassInfo(out ITypeInfo info);
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchTypeInformation
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint locale, out ITypeInfo info);
    }

    [ComImport]
    [ComVisible(true)]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDispatchSink
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint locale, out nint info);

        [PreserveSig]
        int GetIDsOfNames(ref Guid iid, nint names,
            uint count, uint locale, nint ids);

        [PreserveSig]
        int Invoke(int id, ref Guid iid, uint locale, ushort flags,
            ref DISPPARAMS parameters, nint result, nint exception, nint argumentError);
    }
}
