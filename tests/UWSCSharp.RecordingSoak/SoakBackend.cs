using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;

namespace UWSCSharp.RecordingSoak;

// 入力をOSへ送らず、記録スレッドで本番コールバックを呼び出します。
internal sealed class SoakBackend : BasicRecorder.IRecordingHooks, IRecordingHealthMonitor
{
    private readonly Dictionary<int, BasicRecorder.HookCallback> callbacks = [];
    private readonly HookLiveness health = new();
    private readonly nint data = Marshal.AllocHGlobal(32);
    private uint thread;
    private TaskCompletionSource? completion;
    private long nextCycle;
    private int requestedCycles;
    private bool disposed;

    internal long CallbackCount
    {
        get; private set;
    }

    internal long CallbackAllocatedBytes
    {
        get; private set;
    }

    internal int Released
    {
        get; private set;
    }

    public int LastError => Marshal.GetLastWin32Error();

    public nint Register(int type, BasicRecorder.HookCallback callback)
    {
        callbacks.Add(type, callback);
        return type;
    }

    public bool Unregister(nint hook)
    {
        Released++;
        return true;
    }

    public void Start()
    {
        thread = GetCurrentThreadId();
        Marshal.Copy(new byte[32], 0, data, 32);
    }

    public void Hook(int kind, uint inputTime) => health.Hook(kind, Environment.TickCount64);

    internal async Task BatchAsync(int cycles)
    {
        requestedCycles = cycles;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PostThreadMessageW(thread, 0x8007, 0, 0))
        {
            throw new Win32Exception();
        }
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public int ProcessMessage(nint window, uint message, nuint wParam, nint lParam, uint inputTime)
    {
        if (message != 0x8007)
        {
            return 0;
        }
        try
        {
            if (requestedCycles == 0)
            {
                // 実際の押下状態は変更せず、試験記録器だけの開始境界を解除します。
                for (int key = 8; key < 256; key++)
                {
                    Keyboard(key, 2);
                }
                Mouse(0x202, 0, 0);
                Mouse(0x205, 0, 0);
                Mouse(0x208, 0, 0);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int cycle = 0; cycle < requestedCycles; cycle++)
            {
                int key = 65 + (int)(nextCycle % 26);
                int x = (int)(nextCycle % 500);
                int y = (int)(nextCycle % 300);
                Keyboard(160, 1);
                Keyboard(key, 1);
                Mouse(0x200, x, y);
                Mouse(0x201, x, y);
                Mouse(0x200, x + 1, y + 1);
                Mouse(0x202, x + 1, y + 1);
                Keyboard(key, 2);
                Keyboard(160, 2);
                CallbackCount += 8;
                nextCycle++;
            }
            CallbackAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            completion!.TrySetResult();
        }
        catch (Exception error)
        {
            completion!.TrySetException(error);
            throw;
        }
        return health.Missing(Environment.TickCount64);
    }

    private void Keyboard(int key, int state)
    {
        Marshal.WriteInt32(data, 0, key);
        Marshal.WriteInt32(data, 4, 0);
        Marshal.WriteInt32(data, 8, 0);
        Marshal.WriteInt32(data, 12, unchecked((int)Environment.TickCount64));
        callbacks[13](0, state == 1 ? 0x100 : 0x101, data);
        health.Raw(1, Environment.TickCount64);
    }

    private void Mouse(int message, int x, int y)
    {
        Marshal.WriteInt32(data, 0, x);
        Marshal.WriteInt32(data, 4, y);
        Marshal.WriteInt32(data, 8, 0);
        Marshal.WriteInt32(data, 12, 0);
        Marshal.WriteInt32(data, 16, unchecked((int)Environment.TickCount64));
        callbacks[14](0, message, data);
        health.Raw(0, Environment.TickCount64);
    }

    public void Reset() => health.Reset();

    public void Dispose()
    {
        if (!disposed)
        {
            Marshal.FreeHGlobal(data);
            disposed = true;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
}
