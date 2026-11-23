using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RecordingResourceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StoppedRecordersWithLargeBuffersAreNotKeptAlive(int initialState)
    {
        int[] initialButtons = initialState switch
        {
            1 => [3],
            2 => [4],
            3 => [0, 1, 2, 3, 4],
            _ => []
        };
        var references = new List<WeakReference>();
        for (int iteration = 0; iteration < 4; iteration++)
        {
            references.Add(CreateStoppedRecorder(initialButtons));
        }
        for (int pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateStoppedRecorder(int[] initialButtons)
    {
        var hooks = new Hooks(initialButtons);
        using var recorder = new BasicRecorder(hooks);
        recorder.StartAsync().GetAwaiter().GetResult();
        nint data = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[32], 0, data, 32);
            for (int key = 8; key < 256; key++)
            {
                Marshal.WriteInt32(data, 0, key);
                hooks.Keyboard!(0, 0x101, data);
            }
            Marshal.Copy(new byte[32], 0, data, 32);
            hooks.Mouse!(0, 0x202, data);
            hooks.Mouse!(0, 0x205, data);
            hooks.Mouse!(0, 0x208, data);
            // サイドボタンも解放し、開始時入力の待機を必ず終えます。
            Marshal.WriteInt32(data, 8, 1 << 16);
            hooks.Mouse!(0, 0x20c, data);
            Marshal.WriteInt32(data, 8, 2 << 16);
            hooks.Mouse!(0, 0x20c, data);
            Assert.False(recorder.WaitingForInitialRelease);
            Marshal.Copy(new byte[32], 0, data, 32);
            // 実際の入力は送らず、上限に達する大きな保持バッファを作ります。
            for (int index = 0; index < 50_001; index++)
            {
                hooks.Mouse!(0, 0x200, data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
        recorder.Stop();
        Assert.True(recorder.Overflowed);
        Assert.False(recorder.IsRunning);
        Assert.Equal(2, hooks.Released);
        return new WeakReference(recorder);
    }

    private sealed class Hooks(int[] initialButtons) : BasicRecorder.IRecordingHooks
    {
        public IReadOnlyList<int> PressedKeys() => [65, 160];

        public IReadOnlyList<int> PressedButtons() => initialButtons;

        internal BasicRecorder.HookCallback? Mouse
        {
            get; private set;
        }

        internal BasicRecorder.HookCallback? Keyboard
        {
            get; private set;
        }

        internal int Released
        {
            get; private set;
        }

        public int LastError => 0;

        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            if (type == 14)
            {
                Mouse = callback;
            }
            else
            {
                Keyboard = callback;
            }
            return type;
        }

        public bool Unregister(nint hook)
        {
            Released++;
            return true;
        }
    }
}
