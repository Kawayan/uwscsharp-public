using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UWSCSharp.Windows;

internal static partial class NativeComDispatch
{
    [DllImport("UWSCSharp.FloatingPoint.Native.dll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory
        | DllImportSearchPath.ApplicationDirectory)]
    private static extern uint InvokeDispatchWithControl(nint dispatch, int id,
        ref Guid iid, uint locale, ushort flags, ref DISPPARAMS parameters,
        nint result, ref EXCEPINFO exception, out uint argumentError,
        ushort controlWord, out int status);

    [DllImport("UWSCSharp.FloatingPoint.Native.dll", ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory
        | DllImportSearchPath.ApplicationDirectory)]
    private static extern ulong GetControlState();
}
