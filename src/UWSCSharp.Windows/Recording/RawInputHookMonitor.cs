using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

// Raw Inputの登録はプロセス共通なので、WPFから明示的に有効化した場合だけ使います。
internal sealed partial class RawInputHookMonitor : IRecordingHealthMonitor
{
    private readonly HookLiveness liveness = new();
    private nint window;
    private nuint timer;
    private bool registered;
    private readonly Func<nint, (RawPacket Packet, uint Copied)> read;

    internal RawInputHookMonitor() : this(ReadNative)
    {
    }

    internal RawInputHookMonitor(Func<nint, (RawPacket Packet, uint Copied)> read)
    {
        this.read = read;
    }

    public void Start()
    {
        if (window != 0)
        {
            throw new InvalidOperationException("フック監視は既に開始しています。");
        }
        if (Registrations().Any(device => device.Page == 1
            && device.Usage is 2 or 6))
        {
            throw new InvalidOperationException("このプロセスではRaw Inputが既に使用されています。既存の登録を維持して記録を開始しません。");
        }
        liveness.Reset(clearResponses: true);
        window = Native.CreateWindowExW(0, ControlClassNames.Uppercase.Static,
            "UWSC recording health", 0,
            0, 0, 0, 0, -3, 0, 0, 0);
        if (window == 0)
        {
            throw new Win32Exception();
        }
        RawDevice[] devices = Devices(0x100, window);
        if (!RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>()))
        {
            throw new Win32Exception();
        }
        registered = true;
        timer = Native.SetTimer(0, 0, 250, 0);
        if (timer == 0)
        {
            throw new Win32Exception();
        }
    }

    public void Hook(int kind, uint inputTime) => liveness.Hook(kind, Timestamp(inputTime));

    public int ProcessMessage(nint target, uint message, nuint wParam, nint lParam, uint inputTime = 0)
    {
        if (target == window && message == 0xFF)
        {
            (RawPacket packet, uint copied) = read(lParam);
            if (HasActivity(packet, copied))
            {
                liveness.Raw((int)packet.Header.Type, Timestamp(inputTime), Environment.TickCount64);
                return liveness.Missing(Environment.TickCount64);
            }
        }
        if (target == 0 && message == 0x113 && wParam == timer)
        {
            RawDevice[] devices = Registrations();
            if (!devices.Any(device => device.Page == 1 && device.Usage == 2
                && device.Target == window)
                || !devices.Any(device => device.Page == 1 && device.Usage == 6
                && device.Target == window))
            {
                throw new InvalidOperationException("Raw Inputの監視登録が変更されたため記録を停止します。");
            }
            return liveness.Missing(Environment.TickCount64);
        }
        return 0;
    }

    public void Reset() => liveness.Reset();

    private static (RawPacket Packet, uint Copied) ReadNative(nint input)
    {
        uint size = (uint)Marshal.SizeOf<RawPacket>();
        uint copied = GetRawInputData(input, 0x10000003,
            out RawPacket packet, ref size, (uint)Marshal.SizeOf<RawHeader>());
        if (copied == uint.MaxValue)
        {
            throw new Win32Exception();
        }
        return (packet, copied);
    }

    private static long Timestamp(uint inputTime)
    {
        long now = Environment.TickCount64;
        return now - unchecked((uint)now - inputTime);
    }

    internal static bool HasActivity(RawPacket packet, uint copied)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawHeader>();
        uint minimum = headerSize + (packet.Header.Type == 0 ? 24u : 16u);
        if (copied < headerSize || copied != packet.Header.Size
            || copied > Marshal.SizeOf<RawPacket>()
            || (packet.Header.Type <= 1 && copied < minimum))
        {
            throw new InvalidOperationException("Raw Inputのパケット長が不正です。");
        }
        // 空の相対移動やキーボードのオーバーラン通知は途絶の根拠にしません。
        return packet.Header.Type switch
        {
            0 => packet.Data.X != 0 || packet.Data.Y != 0
                || packet.Data.ButtonFlags != 0 || (packet.Data.MouseFlags & 1) != 0,
            1 => packet.Data.Key is > 0 and < 255,
            _ => false
        };
    }

    public void Dispose()
    {
        var errors = new List<Exception>();
        if (timer != 0)
        {
            if (!KillTimer(0, timer))
            {
                errors.Add(new Win32Exception());
            }
            timer = 0;
        }
        if (registered)
        {
            try
            {
                RawDevice[] owned = [.. Registrations().Where(device =>
                    device.Page == 1 && device.Usage is 2 or 6 && device.Target == window)
                    .Select(device => new RawDevice
                    {
                        Page = device.Page, Usage = device.Usage, Flags = 1, Target = 0
                    })];
                if (owned.Length != 0 && !RegisterRawInputDevices(owned,
                    (uint)owned.Length, (uint)Marshal.SizeOf<RawDevice>()))
                {
                    errors.Add(new Win32Exception());
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            registered = false;
        }
        if (window != 0)
        {
            if (!Native.DestroyWindow(window))
            {
                errors.Add(new Win32Exception());
            }
            window = 0;
        }
        if (errors.Count != 0)
        {
            throw new AggregateException("フック監視の解放に失敗しました。", errors);
        }
    }

    private static RawDevice[] Devices(uint flags, nint target) =>
    [
        new() { Page = 1, Usage = 2, Flags = flags, Target = target },
        new() { Page = 1, Usage = 6, Flags = flags, Target = target }
    ];

    private static RawDevice[] Registrations()
    {
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RawDevice>();
        uint result = GetRegisteredRawInputDevices(null, ref count, size);
        if (result == uint.MaxValue && Marshal.GetLastWin32Error() != 122)
        {
            throw new Win32Exception();
        }
        if (count == 0)
        {
            return [];
        }
        var devices = new RawDevice[count];
        result = GetRegisteredRawInputDevices(devices, ref count, size);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception();
        }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice
    {
        public ushort Page;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct RawPayload
    {
        [FieldOffset(0)] public ushort MouseFlags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort Key;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawPacket
    {
        public RawHeader Header;
        public RawPayload Data;
    }
}
