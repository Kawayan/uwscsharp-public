using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有Threadのnative handleを保持し、6段階の優先度を適用します。
/// </summary>
internal sealed partial class WindowsWorkerThreadPriority : IWorkerThreadPriority
{
    private readonly object gate = new();
    private SafeWaitHandle? handle;
    private bool disposed;

    /// <summary>
    /// 呼出元の所有Threadを識別子の再利用から保護します。
    /// </summary>
    public void Initialize()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (handle is not null)
            {
                throw new InvalidOperationException("Threadは初期化済みです。");
            }

            // THREAD_SET_INFORMATIONだけを要求し、他Threadを探索しません。
            SafeWaitHandle opened = OpenThread(0x0020, false, Native.GetCurrentThreadId());
            if (opened.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                opened.Dispose();
                throw new Win32Exception(error);
            }

            handle = opened;
        }
    }

    /// <summary>
    /// 実際に継続を実行する所有Threadへ優先度を適用します。
    /// </summary>
    public void SetPriority(WorkerThreadPriority priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (handle is null)
            {
                throw new InvalidOperationException("Threadが初期化されていません。");
            }

            if (!SetThreadPriority(handle, (int)priority))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>
    /// 優先度変更との競合を防いでhandleを一度だけ解放します。
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            handle?.Dispose();
            handle = null;
        }
    }
}
