using System.IO;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Wpf;

/// <summary>
/// 保存の排他とは別に、監視世代の所有ハンドルを保持します。
/// </summary>
internal sealed class SchedulerMonitorLease : IDisposable
{
    private FileStream? stream;

    private SchedulerMonitorLease(FileStream stream)
    {
        this.stream = stream;
        if (stream.Length == 0)
        {
            JobIdentity = Guid.NewGuid();
            stream.Write(JobIdentity.ToByteArray());
            stream.Flush(flushToDisk: true);
        }
        else
        {
            if (stream.Length != 16)
            {
                throw new InvalidDataException("予約監視のJob識別子が不正です。");
            }
            Span<byte> bytes = stackalloc byte[16];
            stream.ReadExactly(bytes);
            JobIdentity = new Guid(bytes);
            if (JobIdentity == Guid.Empty)
            {
                throw new InvalidDataException("予約監視のJob識別子は空にできません。");
            }
        }
    }

    internal Guid JobIdentity
    {
        get;
    }

    internal SafeFileHandle Handle => ActiveHandle
        ?? throw new ObjectDisposedException(nameof(SchedulerMonitorLease));

    internal Guid Generation { get; } = Guid.NewGuid();

    internal bool IsActive => ActiveHandle is not null;

    private SafeFileHandle? ActiveHandle
    {
        get
        {
            FileStream? current = Volatile.Read(ref stream);
            if (current is null)
            {
                return null;
            }

            try
            {
                SafeFileHandle handle = current.SafeFileHandle;
                return handle.IsClosed || handle.IsInvalid ? null : handle;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    internal static SchedulerMonitorLease? TryAcquire(string? path = null)
    {
        string target = Path.GetFullPath(path
            ?? ApplicationInfo.SettingsPath("schedule.monitor"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        FileStream? acquired = null;
        try
        {
            acquired = new FileStream(target, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new SchedulerMonitorLease(acquired);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            acquired?.Dispose();
            return null;
        }
        catch
        {
            acquired?.Dispose();
            throw;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
