using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 列挙要素をCLR値へ変換する前にVARIANTとして読み取ります。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeComEnumerator : IEnumerator, IDisposable
{
    [ComImport]
    [Guid("00020404-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumVariant
    {
        [PreserveSig]
        int Next(uint count, nint values, out uint fetched);
    }

    private readonly IEnumVariant enumerator;
    private readonly ComVariantValue owner;
    private ComVariantValue? current;
    private bool pending;
    private bool disposed;

    private NativeComEnumerator(ComVariantValue owner)
    {
        this.owner = owner;
        enumerator = ComOperationErrors.Validate(() => (IEnumVariant)owner.Value!);
    }

    internal static NativeComEnumerator Open(object target)
    {
        var owner = (ComVariantValue)NativeComDispatch.Invoke(target,
            "DISPID_NEWENUM", [], [], [], (uint)CultureInfo.CurrentCulture.LCID,
            assign: false, memberId: -4)!;
        try
        {
            if (owner.Type is not (9 or 13) || owner.Value is null)
            {
                throw ComOperationErrors.Record(
                    new InvalidOperationException("COM列挙子が返されませんでした。"));
            }

            return new NativeComEnumerator(owner);
        }
        catch (Exception primary)
        {
            Release(owner, primary);
            throw;
        }
    }

    public object Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ComVariantValue value = current
                ?? throw new InvalidOperationException("現在の列挙要素がありません。");
            pending = false;
            return value;
        }
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleasePending();
        current = null;
        int stride = IntPtr.Size == 8 ? 24 : 16;
        byte[] zero = new byte[stride];
        nint variant = Marshal.AllocCoTaskMem(stride);
        Exception? primary = null;
        try
        {
            Marshal.Copy(zero, 0, variant, stride);
            int hr = enumerator.Next(1, variant, out uint fetched);
            ComOperationErrors.ThrowIfFailed(hr);
            if (!((hr == 0 && fetched == 1) || (hr == 1 && fetched == 0)))
            {
                throw ComOperationErrors.Record(
                    new InvalidOperationException("COM列挙子の返却件数またはHRESULTが不正です。"));
            }

            if (fetched == 0)
            {
                return false;
            }

            current = ComVariantValue.Read(variant);
            pending = true;
            return true;
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            try
            {
                Marshal.ThrowExceptionForHR(Native.VariantClear(variant));
            }
            catch (Exception cleanup)
            {
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.ComEnumerationCleanupErrors"] = cleanup;
                }
                else
                {
                    ReleasePending(cleanup);
                    throw;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(variant);
            }
        }
    }

    public void Reset() => throw new NotSupportedException("列挙子の巻戻しは使用しません。");

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Exception? primary = null;
        try
        {
            ReleasePending();
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            Release(owner, primary);
        }
    }

    private void ReleasePending(Exception? primary = null)
    {
        if (pending && current is ComVariantValue value)
        {
            pending = false;
            Release(value, primary);
        }
    }

    private static void Release(ComVariantValue value, Exception? primary)
    {
        var errors = new List<Exception>();
        if (value.Acquisitions is IReadOnlyList<object> owned)
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

        if (errors.Count > 0)
        {
            var cleanup = new AggregateException("COM列挙要素の解放に失敗しました。", errors);
            if (primary is not null)
            {
                primary.Data["UWSCSharp.ComEnumerationReleaseErrors"] = cleanup;
            }
            else
            {
                throw cleanup;
            }
        }
    }
}
