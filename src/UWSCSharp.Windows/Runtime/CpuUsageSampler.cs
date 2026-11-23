using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// idleを含むkernel時間とuser時間の累積値です。
/// </summary>
internal readonly partial record struct CpuTimes(ulong Idle, ulong Kernel, ulong User, ushort Group = 0)
{
    internal static CpuTimes Read()
    {
        if (!GetThreadGroupAffinity(GetCurrentThread(), out GroupAffinity affinity)
            || !GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            throw WindowsOperationErrors.Record(new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new(idle, kernel, user, affinity.Group);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        internal nuint Mask;
        internal ushort Group;
        internal ushort Reserved1;
        internal ushort Reserved2;
        internal ushort Reserved3;
    }
}

/// <summary>
/// セッションごとの1秒以上の区間からCPU使用率を計算します。
/// </summary>
internal sealed class CpuUsageSampler(Func<CpuTimes> read, TimeProvider clock)
{
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private bool initialized;
    private CpuTimes previous;
    private long timestamp;
    private double last;

    internal double Query(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            long now = clock.GetTimestamp();
            TimeSpan elapsed = initialized ? clock.GetElapsedTime(timestamp, now) : TimeSpan.Zero;
            if (initialized && elapsed >= TimeSpan.Zero && elapsed < MinimumSampleInterval)
            {
                token.ThrowIfCancellationRequested();
                return last;
            }

            CpuTimes current = read();
            token.ThrowIfCancellationRequested();
            double result = 0;
            if (initialized && elapsed >= TimeSpan.Zero && current.Group == previous.Group
                && current.Idle >= previous.Idle && current.Kernel >= previous.Kernel
                && current.User >= previous.User)
            {
                double kernel = current.Kernel - previous.Kernel;
                double user = current.User - previous.User;
                double idle = current.Idle - previous.Idle;
                double total = kernel + user;
                if (total > 0 && idle <= total)
                {
                    result = Math.Clamp((total - idle) * 100 / total, 0, 100);
                }
            }

            previous = current;
            timestamp = now;
            initialized = true;
            last = result;
            return result;
        }
    }
}
