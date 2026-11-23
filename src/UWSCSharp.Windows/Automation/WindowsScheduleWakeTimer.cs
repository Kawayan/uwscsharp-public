using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

internal interface IScheduleWakeTimerPlatform : IDisposable
{
    int Arm(long dueTime);

    void Cancel();
}

/// <summary>
/// 名前なし・継承不可の復帰タイマーを所有します。
/// </summary>
public sealed partial class WindowsScheduleWakeTimer : IDisposable
{
    private const int ErrorNotSupported = 50;
    private readonly object sync = new();
    private readonly IScheduleWakeTimerPlatform platform;
    private bool disposed;
    private DateTimeOffset? armedAt;

    /// <summary>
    /// 無効状態のタイマーを作成します。休止状態には移行しません。
    /// </summary>
    public WindowsScheduleWakeTimer() : this(new NativePlatform())
    {
    }

    internal WindowsScheduleWakeTimer(IScheduleWakeTimerPlatform platform)
    {
        this.platform = platform;
    }

    /// <summary>
    /// 最後に登録したUTC時刻です。現在のシグナルや実際の復帰を表しません。
    /// </summary>
    public DateTimeOffset? ArmedAt
    {
        get
        {
            lock (sync)
            {
                return armedAt;
            }
        }
    }

    /// <summary>
    /// 一回だけの復帰を要求し、未対応なら取り消してfalseを返します。
    /// </summary>
    public bool Arm(DateTimeOffset dueAt)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            long fileTime = dueAt.UtcDateTime.ToFileTimeUtc();
            if (fileTime <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dueAt), "復帰時刻は1601年の開始より後にしてください。");
            }
            try
            {
                int error = platform.Arm(fileTime);
                if (error == ErrorNotSupported)
                {
                    platform.Cancel();
                    armedAt = null;
                    return false;
                }
                armedAt = dueAt.ToUniversalTime();
                return true;
            }
            catch
            {
                // 不確かな登録を残さないよう、最後のハンドルを閉じます。
                DisposeCore();
                throw;
            }
        }
    }

    /// <summary>
    /// 復帰要求を取り消します。失敗時もハンドルを閉じます。
    /// </summary>
    public void Cancel()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                platform.Cancel();
                armedAt = null;
            }
            catch
            {
                DisposeCore();
                throw;
            }
        }
    }

    /// <summary>
    /// 最後のハンドルを閉じ、タイマーを破棄します。
    /// </summary>
    public void Dispose()
    {
        lock (sync)
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (!disposed)
        {
            platform.Dispose();
            armedAt = null;
            disposed = true;
        }
    }

    private sealed partial class NativePlatform : IScheduleWakeTimerPlatform
    {
        private readonly SafeWaitHandle handle;

        internal NativePlatform()
        {
            handle = CreateWaitableTimerExW(0, null, 0, 0x00100002);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "復帰タイマーを作成できません。");
            }
        }

        public int Arm(long dueTime)
        {
            bool success = SetWaitableTimer(handle, ref dueTime, 0, 0, 0, true);
            int error = Marshal.GetLastPInvokeError();
            if (!success)
            {
                throw new Win32Exception(error, "復帰タイマーを登録できません。");
            }
            return error;
        }

        public void Cancel()
        {
            if (!CancelWaitableTimer(handle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "復帰タイマーを取り消せません。");
            }
        }

        public void Dispose() => handle.Dispose();
    }
}
