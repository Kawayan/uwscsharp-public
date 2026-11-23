using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UWSCSharp.Windows;

internal sealed partial class NativeComEventConnection
{
    // HRESULTを変換前に識別し、マーシャリング自体の例外と区別します。
    [ComImport]
    [Guid("00020401-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEventTypeInfo
    {
        [PreserveSig]
        int GetTypeAttr(out nint attribute);

        [PreserveSig]
        int GetTypeComp(out nint compiler);

        [PreserveSig]
        int GetFuncDesc(int index, out nint description);

        [PreserveSig]
        int GetVarDesc(int index, out nint description);

        [PreserveSig]
        int GetNames(int member, nint names, int maximum, out int count);

        [PreserveSig]
        int GetRefTypeOfImplType(int index, out int reference);

        [PreserveSig]
        int GetImplTypeFlags(int index, out IMPLTYPEFLAGS flags);

        [PreserveSig]
        int GetIDsOfNames(nint names, int count, nint members);

        [PreserveSig]
        int Invoke(nint instance, int member, short flags,
            nint parameters, nint result, nint exception, nint argumentError);

        [PreserveSig]
        int GetDocumentation(int member,
            [MarshalAs(UnmanagedType.BStr)] out string name,
            [MarshalAs(UnmanagedType.BStr)] out string documentation,
            out int context,
            [MarshalAs(UnmanagedType.BStr)] out string helpFile);

        [PreserveSig]
        int GetDllEntry(int member, INVOKEKIND kind,
            nint dllName, nint entryName, nint ordinal);

        [PreserveSig]
        int GetRefTypeInfo(int reference, out ITypeInfo information);

        [PreserveSig]
        int AddressOfMember(int member, INVOKEKIND kind, out nint address);

        [PreserveSig]
        int CreateInstance(nint outer, ref Guid interfaceId, out nint instance);

        [PreserveSig]
        int GetMops(int member, [MarshalAs(UnmanagedType.BStr)] out string mops);

        [PreserveSig]
        int GetContainingTypeLib(out ITypeLib library, out int index);
    }
}
