using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 送信前のHDROPを所有し、送信成功時に所有権を手放します。
/// </summary>
internal sealed class FileDropMemory : SafeHandle
{
    private FileDropMemory() : base(0, ownsHandle: true)
    {
    }

    /// <summary>
    /// 未割当てのハンドルかどうかを取得します。
    /// </summary>
    public override bool IsInvalid => handle == 0;

    internal static FileDropMemory Create(IReadOnlyList<string> paths, int x, int y,
        CancellationToken token = default)
    {
        byte[] payload = FileDropPayload.Create(paths, x, y, token);
        token.ThrowIfCancellationRequested();
        var owner = new FileDropMemory();
        try
        {
            owner.SetHandle(Native.GlobalAlloc(0x42, (nuint)payload.Length));
            if (owner.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint pointer = Native.GlobalLock(owner.handle);
            if (pointer == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                Marshal.Copy(payload, 0, pointer, payload.Length);
            }
            finally
            {
                if (!Native.GlobalUnlock(owner.handle))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw new Win32Exception(error);
                    }
                }
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal void Post(nint window, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        token.ThrowIfCancellationRequested();
        if (window == 0 || window == 0xffff)
        {
            throw new ArgumentException("送信先には個別のウィンドウを指定してください。", nameof(window));
        }

        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            if (!Native.PostMessageW(window, 0x233, handle, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            SetHandleAsInvalid();
        }
        finally
        {
            if (retained)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle() => Native.GlobalFree(handle) == 0;
}
