using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// COM引数と返値のVARIANT型を保持して呼び出します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeComDispatch
{
    [ThreadStatic]
    private static ushort? outerControlBaseline;

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint locale, out nint info);

        [PreserveSig]
        int GetIDsOfNames(ref Guid id,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] names,
            uint count, uint locale,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] int[] ids);

        [PreserveSig]
        int Invoke(int id, ref Guid iid, uint locale, ushort flags,
            ref DISPPARAMS parameters, nint result, ref EXCEPINFO exception, out uint error);
    }

    internal static object? Invoke(object target, string member, object?[] arguments,
        IReadOnlyList<bool> byReference, string[] names, uint locale, bool assign = true,
        int? memberId = null, ushort? controlWord = null)
    {
        var dispatch = (IDispatch)target;
        Guid empty = Guid.Empty;
        string[] lookup = [member, .. names];
        int[] ids = new int[lookup.Length];
        if (memberId is int knownId)
        {
            ids[0] = knownId;
        }
        else
        {
            ComOperationErrors.ThrowIfFailed(dispatch.GetIDsOfNames(ref empty, lookup,
                (uint)lookup.Length, locale, ids));
        }

        int stride = IntPtr.Size == 8 ? 24 : 16;
        nint variants = 0;
        nint storage = 0;
        nint namedIds = 0;
        nint result = 0;
        nint dispatchPointer = 0;
        Exception? primary = null;
        ComVariantValue? returned = null;
        var errors = new List<Exception>();
        try
        {
            variants = Allocate(arguments.Length * stride);
            storage = Allocate(arguments.Length * stride);
            int namedCount = names.Length + (assign ? 1 : 0);
            namedIds = Allocate(namedCount * sizeof(int));
            result = Allocate(stride);
            if (assign)
            {
                Marshal.WriteInt32(namedIds, -3);
            }

            for (int i = 0; i < names.Length; i++)
            {
                Marshal.WriteInt32(namedIds, (i + (assign ? 1 : 0)) * sizeof(int), ids[i + 1]);
            }

            int[] order = assign
                ? [arguments.Length - 1, .. Enumerable.Range(0, names.Length),
                    .. Enumerable.Range(names.Length, arguments.Length - names.Length - 1).Reverse()]
                : [.. Enumerable.Range(0, names.Length),
                    .. Enumerable.Range(names.Length, arguments.Length - names.Length).Reverse()];
            for (int i = 0; i < order.Length; i++)
            {
                int source = order[i];
                nint destination = variants + i * stride;
                if (source < byReference.Count && byReference[source])
                {
                    nint cell = storage + source * stride;
                    object? value = arguments[source] is VariantWrapper wrapper
                        ? wrapper.WrappedObject : arguments[source];
                    ComVariantValue.Write(value, cell);
                    Marshal.WriteInt16(destination, 0x400c);
                    Marshal.WriteIntPtr(destination, 8, cell);
                }
                else
                {
                    ComVariantValue.Write(arguments[source], destination);
                }
            }

            var parameters = new DISPPARAMS
            {
                rgvarg = variants,
                rgdispidNamedArgs = namedIds,
                cArgs = arguments.Length,
                cNamedArgs = namedCount
            };
            var exception = new EXCEPINFO();
            ushort flags = assign ? (ushort)12 : (ushort)3;
            int hr;
            uint argumentError;
            ushort? previousBaseline = outerControlBaseline;
            if ((controlWord ?? previousBaseline) is ushort word)
            {
                dispatchPointer = Marshal.GetIDispatchForObject(target);
                // 再入先で設定を解除した場合は、最外の呼出し前の制御語を使います。
                outerControlBaseline = previousBaseline
                    ?? unchecked((ushort)GetControlState());
                uint fault;
                try
                {
                    fault = InvokeDispatchWithControl(dispatchPointer, ids[0],
                        ref empty, locale, flags, ref parameters, result,
                        ref exception, out argumentError, word, out hr);
                }
                finally
                {
                    outerControlBaseline = previousBaseline;
                }

                if (fault != 0)
                {
                    throw ComOperationErrors.Record(new COMException(
                        $"COM呼出し中に浮動小数点例外が発生しました。0x{fault:X8}",
                        unchecked((int)fault)));
                }
            }
            else
            {
                hr = dispatch.Invoke(ids[0], ref empty, locale, flags,
                    ref parameters, result, ref exception, out argumentError);
            }
            if (hr < 0)
            {
                throw ComOperationErrors.Record(
                    ComErrorInfo.Create(hr, ref exception, argumentError, order, member));
            }

            var outputBudget = new ComValueBudget();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i < byReference.Count && byReference[i])
                {
                    arguments[i] = ComVariantValue.Read(storage + i * stride,
                        budget: outputBudget);
                }
            }

            returned = ComVariantValue.Read(result, budget: outputBudget);
            return returned.Value;
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            if (dispatchPointer != 0)
            {
                Marshal.Release(dispatchPointer);
            }

            Clear(variants, arguments.Length, stride, errors);
            Clear(storage, arguments.Length, stride, errors);
            Clear(result, 1, stride, errors);
            Marshal.FreeCoTaskMem(namedIds);
            if (errors.Count > 0)
            {
                if (returned?.Acquisitions is IReadOnlyList<object> owned)
                {
                    for (int i = owned.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            Marshal.ReleaseComObject(owned[i]);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex);
                        }
                    }
                }

                var cleanup = new AggregateException("COM VARIANTの解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.ComCleanupErrors"] = cleanup;
                }
                else
                {
                    ExceptionDispatchInfo.Capture(cleanup).Throw();
                }
            }
        }
    }

    private static nint Allocate(int bytes)
    {
        if (bytes == 0)
        {
            return 0;
        }

        byte[] cleared = new byte[bytes];
        nint memory = Marshal.AllocCoTaskMem(bytes);
        try
        {
            Marshal.Copy(cleared, 0, memory, bytes);
            return memory;
        }
        catch
        {
            Marshal.FreeCoTaskMem(memory);
            throw;
        }
    }

    private static void Clear(nint memory, int count, int stride, List<Exception> errors)
    {
        if (memory == 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(Native.VariantClear(memory + i * stride));
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(memory);
        }
    }
}
