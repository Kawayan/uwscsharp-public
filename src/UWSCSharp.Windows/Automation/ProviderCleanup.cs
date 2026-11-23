using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// ACC/UIAの局所解放を継続し、主例外を保持します。
/// </summary>
internal struct ProviderCleanup(Exception? primary,
    ProviderCleanupOperations? operations = null)
{
    /// <summary>
    /// 引数省略時も解放処理を初期化します。
    /// </summary>
    public ProviderCleanup() : this(null)
    {
    }

    internal const string DataKey = "UWSCSharp.ProviderCleanupErrors";

    private readonly ProviderCleanupOperations nativeOperations =
        operations ?? ProviderCleanupOperations.Current;
    private List<Exception>? errors;

    /// <summary>
    /// catchへ入らず、解放時に保持する主例外を記録します。
    /// </summary>
    internal static bool PreservePrimary(Exception failure,
        ref Exception? primary)
    {
        primary = failure;
        return false;
    }

    internal void Release(object value)
    {
        try
        {
            nativeOperations.Release(value);
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void ReleaseComOnly(object? value)
    {
        try
        {
            if (value is not null && nativeOperations.IsComObject(value))
            {
                nativeOperations.Release(value);
            }
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void ReleasePointer(nint value)
    {
        try
        {
            nativeOperations.ReleasePointer(value);
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void ReleasePointer(ref nint value)
    {
        nint previous = value;
        value = 0;
        if (previous != 0)
        {
            ReleasePointer(previous);
        }
    }

    internal void ClearVariant(nint value)
    {
        try
        {
            Marshal.ThrowExceptionForHR(
                nativeOperations.ClearVariant(value), (nint)(-1));
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void ClearVariant(ref NativeButtonVariant value)
    {
        try
        {
            Marshal.ThrowExceptionForHR(
                nativeOperations.ClearVariant(ref value), (nint)(-1));
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void Free(nint value)
    {
        try
        {
            nativeOperations.Free(value);
        }
        catch (Exception failure)
        {
            (errors ??= []).Add(failure);
        }
    }

    internal void Finish()
    {
        if (errors is null)
        {
            return;
        }

        if (primary is null)
        {
            throw new AggregateException("ACC/UIAの局所解放に失敗しました。", errors);
        }

        if (primary.Data[DataKey] is AggregateException previous)
        {
            errors.InsertRange(0, previous.InnerExceptions);
        }

        primary.Data[DataKey] = new AggregateException(
            "ACC/UIAの局所解放に失敗しました。", errors);
    }
}

/// <summary>
/// 解放失敗を注入できる、native解放の境界です。
/// </summary>
internal partial class ProviderCleanupOperations
{
    internal static readonly ProviderCleanupOperations Native = new();
    private static readonly AsyncLocal<ProviderCleanupOperations?> Local = new();

    internal static ProviderCleanupOperations Current => Local.Value ?? Native;

    /// <summary>
    /// 検証用の解放処理を、現在の実行contextだけへ接続します。
    /// </summary>
    internal static IDisposable UseForTesting(ProviderCleanupOperations operations)
    {
        ProviderCleanupOperations? previous = Local.Value;
        Local.Value = operations;
        return new Restore(previous);
    }

    private sealed class Restore(ProviderCleanupOperations? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                Local.Value = previous;
                disposed = true;
            }
        }
    }

    internal virtual bool IsComObject(object value) => Marshal.IsComObject(value);

    internal virtual void Release(object value) => Marshal.ReleaseComObject(value);

    internal virtual void ReleasePointer(nint value) => Marshal.Release(value);

    internal virtual int ClearVariant(nint value) => VariantClear(value);

    internal virtual int ClearVariant(ref NativeButtonVariant value) =>
        NativeButtonAccessibleApi.Clear(ref value);

    internal virtual void Free(nint value) => Marshal.FreeCoTaskMem(value);
}
