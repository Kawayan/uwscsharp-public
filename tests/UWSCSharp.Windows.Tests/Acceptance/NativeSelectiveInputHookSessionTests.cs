using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputHookSessionTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(3, 0, 0)]
    [InlineData(3, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 0)]
    [InlineData(3, 1, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(2, 0, 2)]
    [InlineData(3, 0, 2)]
    [InlineData(1, 1, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 2)]
    public async Task DedicatedOwnerPumpsInputAndStopsBeforeExit(int selection, int extra, int targetScope)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var received = new ConcurrentQueue<(uint Message, nint Key)>();
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(window =>
        {
            window.ObserveInput(window.Root, (message, key, _) =>
            {
                received.Enqueue((message, key));
                if (message == 0x101 && key == 0x86)
                {
                    Volatile.Read(ref sentinel).TrySetResult();
                }
            });
        }, dpiAwarenessContext: -4);
        await using PumpedControlFixture other = await PumpedControlFixture.CreateAsync(_ => { },
            dpiAwarenessContext: -4);
        Owner? hookOwner = null;
        try
        {
            await Sample(1, 1);
            hookOwner = new Owner(targetScope == 2 ? 0 : targetScope == 1 ? other.Root : fixture.Root,
                (SelectiveInputDevices)selection);
            await hookOwner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Sample(targetScope == 1 || extra == 1 || (selection & 1) == 0 ? 1 : 0,
                targetScope == 1 || extra == 1 || (selection & 2) == 0 ? 1 : 0,
                targetScope == 2 && extra == 0 && (selection & 2) != 0);
            await hookOwner.StopAsync();
            await Sample(1, 1);
        }
        finally
        {
            if (hookOwner is not null)
            {
                await hookOwner.StopAsync();
            }
        }

        async Task Sample(int expectedKeys, int expectedClicks, bool blockMovement = false)
        {
            fixture.Invoke(() =>
            {
                received.Clear();
                Volatile.Write(ref sentinel,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                Assert.True(Native.SetForegroundWindow(fixture.Root));
                SetFocus(fixture.Root);
            });
            using (var dpi = new PhysicalDpiScope())
            {
                var point = new Native.Point(80, 80);
                Assert.True(Native.ClientToScreen(fixture.Root, ref point));
                Assert.True(Native.SetPhysicalCursorPos(point.X, point.Y));
                Assert.Equal(fixture.Root, Native.WindowFromPoint(point));
            }

            Assert.Equal(fixture.Root, Native.GetForegroundWindow());
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point before));
            Native.Input[] inputs = [Mouse(2), Mouse(4), Mouse(8), Mouse(16),
                Mouse(32), Mouse(64), Mouse(128, 1), Mouse(256, 1),
                Mouse(128, 2), Mouse(256, 2), Mouse(0x800, 120),
                Mouse(0x1000, 120), Key(0x87, 0, (nuint)extra),
                Key(0x87, 2, (nuint)extra), Mouse(1, 0, 20),
                Key(0x86, 0, 1), Key(0x86, 2, 1)];
            Assert.Equal((uint)inputs.Length,
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()));
            await Volatile.Read(ref sentinel).Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Invoke(() => { });
            hookOwner?.CheckFailure();
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point after));
            Assert.Equal(blockMovement, (before.X, before.Y) == (after.X, after.Y));
            (uint Message, nint Key)[] events = [.. received];
            Assert.Equal(expectedKeys, events.Count(e => e.Message == 0x100 && e.Key == 0x87));
            Assert.Equal(expectedKeys, events.Count(e => e.Message == 0x101 && e.Key == 0x87));
            Assert.Equal(expectedClicks, events.Count(e => e.Message is 0x201 or 0x203));
            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x202));
            Assert.Equal(expectedClicks, events.Count(e => e.Message is 0x204 or 0x206));
            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x205));
            Assert.Equal(expectedClicks, events.Count(e => e.Message is 0x207 or 0x209));
            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x208));
            int[] buttons = [1, 2];
            foreach (int button in buttons)
            {
                Assert.Equal(expectedClicks, events.Count(e =>
                    (e.Message is 0x20b or 0x20d) && ((long)e.Key >> 16) == button));
                Assert.Equal(expectedClicks, events.Count(e =>
                    e.Message == 0x20c && ((long)e.Key >> 16) == button));
            }

            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x20a));
            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x20e));
        }

        Native.Input Mouse(uint flags, uint data = 0, int x = 0) => new()
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput { X = x, Flags = flags, Data = data, Extra = (nuint)extra }
            }
        };
    }

    private sealed class Owner
    {
        private readonly SelectiveInputHookOwner owner;

        internal Owner(nint target, SelectiveInputDevices devices)
        {
            owner = new SelectiveInputHookOwner(() => new NativeSelectiveInputHookSession(target, devices));
        }

        internal Task Ready => owner.Ready;

        internal void CheckFailure()
        {
            if (owner.Failure is { } error)
            {
                throw error;
            }
        }

        internal async Task StopAsync()
        {
            await owner.DisposeAsync();
            Assert.False(owner.IsAlive);
        }
    }

    private static Native.Input Key(ushort key, uint flags, nuint extra) => new()
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags, Extra = extra }
        }
    };

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
}
