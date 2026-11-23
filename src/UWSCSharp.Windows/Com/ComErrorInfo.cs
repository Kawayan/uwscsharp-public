using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// COMの例外情報と引数位置を元の例外に保持します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ComErrorInfo
{
    [ComImport]
    [Guid("1CF2B120-547D-101B-8E65-08002B2BD119")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IErrorInfo
    {
        [PreserveSig]
        int GetGUID(out Guid id);

        [PreserveSig]
        int GetSource([MarshalAs(UnmanagedType.BStr)] out string? source);

        [PreserveSig]
        int GetDescription([MarshalAs(UnmanagedType.BStr)] out string? description);

        [PreserveSig]
        int GetHelpFile([MarshalAs(UnmanagedType.BStr)] out string? file);

        [PreserveSig]
        int GetHelpContext(out uint context);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int FillIn(ref EXCEPINFO info);

    internal static COMException Create(int hr, ref EXCEPINFO info,
        uint argumentError, IReadOnlyList<int> order, string member)
    {
        var errors = new List<Exception>();
        ReadThreadError(ref info, errors);
        Exception? deferredError = null;
        if (hr == unchecked((int)0x80020009) && info.pfnDeferredFillIn != 0)
        {
            EXCEPINFO original = info;
            try
            {
                FillIn callback = Marshal.GetDelegateForFunctionPointer<FillIn>(info.pfnDeferredFillIn);
                Marshal.ThrowExceptionForHR(callback(ref info));
            }
            catch (Exception ex)
            {
                deferredError = ex;
                info = original;
            }
        }

        var failure = new COMException(info.bstrDescription ?? $"COM呼出しに失敗しました。HRESULT=0x{hr:X8}",
            info.scode != 0 ? info.scode : hr)
        {
            Source = info.bstrSource,
            HelpLink = info.bstrHelpFile
        };
        failure.Data["UWSCSharp.ComInvokeHResult"] = hr;
        failure.Data["UWSCSharp.ComExceptionCode"] = unchecked((ushort)info.wCode);
        failure.Data["UWSCSharp.ComHelpContext"] = info.dwHelpContext;
        failure.Data["UWSCSharp.ComMember"] = member;
        if (hr is unchecked((int)0x80020004) or unchecked((int)0x80020005)
            && argumentError < order.Count)
        {
            failure.Data["UWSCSharp.ComArgumentIndex"] = order[(int)argumentError];
        }

        if (deferredError is not null)
        {
            failure.Data["UWSCSharp.ComDeferredError"] = deferredError;
        }

        if (errors.Count > 0)
        {
            failure.Data["UWSCSharp.ComErrorInfoErrors"] = new AggregateException(errors);
        }

        return failure;
    }

    private static void ReadThreadError(ref EXCEPINFO info, List<Exception> errors)
    {
        IErrorInfo? native = null;
        try
        {
            int hr = GetErrorInfo(0, out native);
            if (hr == 1)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(hr);
            if (native is null)
            {
                return;
            }

            ReadString(() =>
            {
                int code = native.GetSource(out string? value);
                return (code, value);
            }, ref info.bstrSource, errors);
            ReadString(() =>
            {
                int code = native.GetDescription(out string? value);
                return (code, value);
            }, ref info.bstrDescription, errors);
            ReadString(() =>
            {
                int code = native.GetHelpFile(out string? value);
                return (code, value);
            }, ref info.bstrHelpFile, errors);
            try
            {
                Marshal.ThrowExceptionForHR(native.GetHelpContext(out uint context));
                if (info.dwHelpContext == 0)
                {
                    info.dwHelpContext = unchecked((int)context);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
        finally
        {
            if (native is not null && Marshal.IsComObject(native))
            {
                try
                {
                    Marshal.ReleaseComObject(native);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }
    }

    private static void ReadString(Func<(int Code, string? Value)> read,
        ref string destination, List<Exception> errors)
    {
        try
        {
            (int Code, string? Value) result = read();
            Marshal.ThrowExceptionForHR(result.Code);
            if (result.Value is not null)
            {
                destination ??= result.Value;
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }
}
