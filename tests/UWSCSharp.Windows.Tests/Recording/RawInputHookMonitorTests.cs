using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RawInputHookMonitorTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, false)]
    [InlineData(0, 0, 1, 0, 0, 0, true)]
    [InlineData(0, 0, 0, 0, 0x400, 0, true)]
    [InlineData(0, 0, 0, 0, 0, 1, true)]
    [InlineData(0, 0, 0, 0, 0, 4, false)]
    [InlineData(1, 65, 0, 0, 0, 0, true)]
    [InlineData(1, 255, 0, 0, 0, 0, false)]
    [InlineData(1, 0, 0, 0, 0, 0, false)]
    public void RawPacketActivityIgnoresEmptyMovementAndKeyboardOverrun(
        uint type, ushort key, int x, int y, ushort buttons, ushort flags, bool expected)
    {
        uint size = (uint)Marshal.SizeOf<RawInputHookMonitor.RawHeader>()
            + (type == 0 ? 24u : 16u);
        var packet = new RawInputHookMonitor.RawPacket
        {
            Header = new() { Type = type, Size = size },
            Data = new() { Key = key, X = x, Y = y, ButtonFlags = buttons, MouseFlags = flags }
        };
        Assert.Equal(expected, RawInputHookMonitor.HasActivity(packet, size));
        Assert.Throws<InvalidOperationException>(() => RawInputHookMonitor.HasActivity(packet, size - 1));
    }

    [Fact]
    public void NativeRegistrationTimerAndWindowAreReleased()
    {
        RunOnThread(() =>
        {
            using var monitor = new RawInputHookMonitor();
            monitor.Start();
            Device[] registrations = Registrations();
            Assert.Equal(2, registrations.Length);
            nint window = registrations[0].Target;
            Assert.NotEqual(0, window);
            Assert.All(registrations, device => Assert.Equal(window, device.Target));
            Assert.True(IsWindow(window));
            Assert.Throws<InvalidOperationException>(monitor.Start);
            monitor.Dispose();
            Assert.Empty(Registrations());
            Assert.False(IsWindow(window));
            monitor.Start();
        });
    }

    [Fact]
    public void AnotherMonitorCannotOverwriteExistingRegistration()
    {
        RunOnThread(() =>
        {
            using var first = new RawInputHookMonitor();
            using var second = new RawInputHookMonitor();
            first.Start();
            nint target = Registrations()[0].Target;
            Assert.Throws<InvalidOperationException>(second.Start);
            second.Dispose();
            Assert.All(Registrations(), device => Assert.Equal(target, device.Target));
        });
    }

    [Fact]
    public void InvalidRawHandleIsReported()
    {
        RunOnThread(() =>
        {
            using var monitor = new RawInputHookMonitor();
            monitor.Start();
            nint target = Registrations()[0].Target;
            Assert.Throws<Win32Exception>(() => monitor.ProcessMessage(target, 0xFF, 1, 0));
        });
    }

    [Fact]
    public void RegistrationTakeoverIsDetectedAndExternalOwnerIsPreserved()
    {
        RunOnThread(() =>
        {
            using var monitor = new RawInputHookMonitor();
            nint external = CreateWindowExW(0, "STATIC", "raw test", 0,
                0, 0, 0, 0, -3, 0, 0, 0);
            Assert.NotEqual(0, external);
            try
            {
                monitor.Start();
                Assert.True(RegisterRawInputDevices(
                    [new Device { Page = 1, Usage = 2, Flags = 0x100, Target = external }],
                    1, (uint)Marshal.SizeOf<Device>()));
                Assert.Throws<InvalidOperationException>(() => ProcessNextTimer(monitor));
                monitor.Dispose();
                Device remaining = Assert.Single(Registrations());
                Assert.Equal(external, remaining.Target);
                Assert.Equal(2, remaining.Usage);
            }
            finally
            {
                RegisterRawInputDevices([new Device { Page = 1, Usage = 2, Flags = 1 }],
                    1, (uint)Marshal.SizeOf<Device>());
                DestroyWindow(external);
            }
        });
    }

    [Fact]
    public void RecorderOwnsNativeMonitorForItsWholeLifetime()
    {
        RunOnThread(() =>
        {
            using var recorder = new BasicRecorder(0, false, null, 0, monitorHookHealth: true);
            recorder.StartAsync().GetAwaiter().GetResult();
            Assert.Equal(2, Registrations().Length);
            recorder.Stop();
            Assert.Empty(Registrations());
            Assert.False(recorder.IsRunning);
        });
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void RawMessageAndNativeTimerDriveLivenessDecision(bool acknowledge, int expected)
    {
        RunOnThread(() =>
        {
            uint size = (uint)Marshal.SizeOf<RawInputHookMonitor.RawHeader>() + 16;
            var packet = new RawInputHookMonitor.RawPacket
            {
                Header = new() { Type = 1, Size = size },
                Data = new() { Key = 65 }
            };
            using var monitor = new RawInputHookMonitor(_ => (packet, size));
            monitor.Start();
            nint target = Registrations()[0].Target;
            uint timestamp = (uint)Environment.TickCount64;
            Assert.Equal(0, monitor.ProcessMessage(target, 0xFF, 1, 42, timestamp));
            if (acknowledge)
            {
                monitor.Hook(1, timestamp);
            }
            long deadline = Environment.TickCount64 + 1300;
            int result = 0;
            while (Environment.TickCount64 < deadline)
            {
                result |= ProcessNextTimer(monitor);
            }
            Assert.Equal(expected, result);
            monitor.Reset();
            Assert.Equal(0, ProcessNextTimer(monitor));
        });
    }

    private static int ProcessNextTimer(RawInputHookMonitor monitor)
    {
        long deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (PeekMessageW(out Message message, 0, 0x113, 0x113, 1))
            {
                return monitor.ProcessMessage(message.Window, message.Id,
                    message.WParam, message.LParam, message.Time);
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException("試験用の監視タイマーが届きませんでした。");
    }

    private static Device[] Registrations()
    {
        uint count = 16;
        var devices = new Device[count];
        uint copied = GetRegisteredRawInputDevices(devices, ref count, (uint)Marshal.SizeOf<Device>());
        Assert.NotEqual(uint.MaxValue, copied);
        return [.. devices.Take((int)copied)];
    }

    private static void RunOnThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception error)
            {
                failure = error;
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Device
    {
        public ushort Page;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices(
        [Out] Device[] devices, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className,
        string name, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Message message, nint window,
        uint minimum, uint maximum, uint remove);
}
