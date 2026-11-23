using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

internal interface IButtonAccessibleSource
{
    bool Available() => true;

    int FromPoint(Native.Point point, out nint result,
        ref NativeButtonVariant child);

    int FromWindow(nint root, out nint result);
}

/// <summary>
/// 状態・操作wrapperの一回の呼出しでMSAA参照を所有します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeButtonAccessibleSession(
    IButtonAccessibleSource source) : IDisposable
{
    private nint accessible;
    private NativeButtonVariant child;
    private bool disposed;

    internal bool ReadPoint(Native.Point point, ref int role,
        out int state, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        state = 0;
        token.ThrowIfCancellationRequested();
        int result = source.FromPoint(point, out accessible, ref child);
        token.ThrowIfCancellationRequested();
        if (result != 0)
        {
            // 失敗時のoutも所有し、VARIANTはwrapper終了まで保持します。
            return false;
        }

        bool found = NativeButtonAccessibleNode.Read(accessible, child,
            point, token, out int foundRole, out state);
        if (found)
        {
            role = foundRole;
        }

        NativeButtonAccessibleApi.Release(ref accessible);
        ClearChild();
        return found;
    }

    internal void ReadRoot(nint root, Native.Point point, ref int role,
        out int state, CancellationToken token)
    {
        AcquireRoot(root, token);
        state = 0;
        if (accessible != 0)
        {
            if (NativeButtonAccessibleWalk.Read(accessible, point, token,
                out int foundRole, out state))
            {
                role = foundRole;
            }

            NativeButtonAccessibleApi.Release(ref accessible);
        }
    }

    internal bool ActRoot(nint root, Native.Point point,
        IListSelectionPlatform platform, CancellationToken token)
    {
        AcquireRoot(root, token);
        bool found = accessible != 0 && NativeButtonAccessibleWalk.Act(
            accessible, point, platform, token);
        NativeButtonAccessibleApi.Release(ref accessible);
        return found;
    }

    private void AcquireRoot(nint root, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        NativeButtonAccessibleApi.Release(ref accessible);
        // HRESULTではなくout参照の有無で探索を決めます。
        source.FromWindow(root, out accessible);
        token.ThrowIfCancellationRequested();
    }

    private void ClearChild()
        => Marshal.ThrowExceptionForHR(
            NativeButtonAccessibleApi.Clear(ref child));

    public void Dispose() => Dispose(null);

    internal void Dispose(Exception? primary)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var cleanup = new ProviderCleanup(primary);
        cleanup.ClearVariant(ref child);
        cleanup.ReleasePointer(ref accessible);
        cleanup.Finish();
    }
}
