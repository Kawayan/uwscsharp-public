using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputHookCallbacksTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 0, true)]
    [InlineData(3, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 1, true)]
    public async Task NativeCallbacksFilterOwnedWindowAndRelease(int selection, int extra, bool outside)
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
        SelectiveInputHookRegistration? registration = null;
        SelectiveInputHookCallbacks? callbacks = null;
        try
        {
            await Sample(1, 1);
            fixture.Invoke(() =>
            {
                var context = new NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree.Capture());
                callbacks = new SelectiveInputHookCallbacks(outside ? other.Root : fixture.Root,
                    (SelectiveInputDevices)selection, context);
                registration = new SelectiveInputHookRegistration(new NativeSelectiveInputHookBackend(),
                    callbacks.Keyboard, callbacks.Mouse);
                registration.Register((SelectiveInputDevices)selection);
            });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Sample(outside || extra == 1 || (selection & 1) == 0 ? 1 : 0,
                outside || extra == 1 || (selection & 2) == 0 ? 1 : 0);
            fixture.Invoke(() =>
            {
                callbacks!.ThrowIfFaulted();
                registration!.Unregister();
                Assert.False(registration.IsCallbackRooted);
            });
            await Sample(1, 1);
        }
        finally
        {
            fixture.Invoke(() => registration?.Unregister());
        }

        async Task Sample(int expectedKeys, int expectedClicks)
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
            Native.Input[] inputs = [Mouse(2), Mouse(4), Key(0x87, 0, (nuint)extra),
                Key(0x87, 2, (nuint)extra), Key(0x86, 0, 1), Key(0x86, 2, 1)];
            Assert.Equal((uint)inputs.Length,
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()));
            await Volatile.Read(ref sentinel).Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Invoke(() => callbacks?.ThrowIfFaulted());
            (uint Message, nint Key)[] events = [.. received];
            Assert.Equal(expectedKeys, events.Count(e => e.Message == 0x100 && e.Key == 0x87));
            Assert.Equal(expectedKeys, events.Count(e => e.Message == 0x101 && e.Key == 0x87));
            Assert.Equal(expectedClicks, events.Count(e => e.Message is 0x201 or 0x203));
            Assert.Equal(expectedClicks, events.Count(e => e.Message == 0x202));
        }

        Native.Input Mouse(uint flags) => new()
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput { Flags = flags, Extra = (nuint)extra }
            }
        };
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
