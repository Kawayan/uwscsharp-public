using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputEmergencyCallbackTests
{
    [Fact]
    public async Task NewerMonitorCanConsumeReleaseForAnOlderPendingMonitor()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var first = new SelectiveInputEmergencyStop(registry);
        var second = new SelectiveInputEmergencyStop(registry);
        var older = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, new Context());
        older.ConfigureEmergency(first);
        older.ResetEmergency();
        first.SetActive(true);
        using var data = new Buffer();
        try
        {
            foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
            {
                Key(older, data, key);
            }
            Assert.True(older.IsConsumingEmergency);
            first.SetActive(false);
            first.SetRetained(true);
            await first.DisposeExecutionAsync();
            var newer = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All,
                new Context());
            newer.ConfigureEmergency(second);
            newer.ResetEmergency();
            second.SetActive(true);
            Assert.Equal(1, Key(newer, data, 122, false));
            Assert.False(older.IsConsumingEmergency);
            foreach (int key in new[] { 0xA2, 0xA4, 0xA0 })
            {
                Key(newer, data, key, false);
            }
            Key(newer, data, 122);
            Assert.False(second.IsRequested);
        }
        finally
        {
            await first.DisposeExecutionAsync();
            await second.DisposeExecutionAsync();
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChordPrecedesFilteringAndSuspendsEveryTarget(int selection)
    {
        var devices = (SelectiveInputDevices)selection;
        var registry = new SelectiveInputEmergencyRegistry();
        var emergency = new SelectiveInputEmergencyStop(registry);
        var other = new SelectiveInputEmergencyStop(registry);
        var context = new Context();
        var otherContext = new Context();
        var callbacks = new SelectiveInputHookCallbacks(7, devices, context);
        var otherCallbacks = new SelectiveInputHookCallbacks(7, SelectiveInputDevices.All, otherContext);
        callbacks.ConfigureEmergency(emergency);
        otherCallbacks.ConfigureEmergency(other);
        callbacks.ResetEmergency();
        otherCallbacks.ResetEmergency();
        emergency.SetActive(true);
        other.SetActive(true);
        using var data = new Buffer();
        try
        {
            Assert.Equal((devices & SelectiveInputDevices.Keyboard) != 0 ? 1 : 123,
                Key(callbacks, data, 65));
            Assert.Equal(1, Key(otherCallbacks, data, 65));
            foreach (int modifier in new[] { 0xA2, 0xA4, 0xA0 })
            {
                Key(callbacks, data, modifier);
            }

            Assert.Equal(1, Key(callbacks, data, 122));
            Assert.True(emergency.IsRequested);
            Assert.True(other.IsRequested);
            int queries = context.Queries + otherContext.Queries;
            Assert.Equal(123, Key(callbacks, data, 0xA2, false));
            Assert.Equal(1, Key(callbacks, data, 122));
            Assert.Equal(1, Key(callbacks, data, 122, false));
            Assert.Equal(123, Key(callbacks, data, 65));
            Assert.Equal(123, Key(otherCallbacks, data, 65));
            data.Mouse();
            Assert.Equal(123, callbacks.Mouse(0, 0x204, data.Pointer));
            Assert.Equal(123, otherCallbacks.Mouse(0, 0x204, data.Pointer));
            Assert.Equal(queries, context.Queries + otherContext.Queries);
            Assert.Equal(1, context.Snapshots);
            Assert.Equal(1, otherContext.Snapshots);
            await SelectiveInputEmergencyStopTests.WaitAsync(() =>
                !emergency.IsDispatching && !other.IsDispatching);
            Assert.True(emergency.Token.IsCancellationRequested);
            Assert.True(other.Token.IsCancellationRequested);
        }
        finally
        {
            await emergency.DisposeExecutionAsync();
            await other.DisposeExecutionAsync();
        }
    }

    [Fact]
    public async Task InjectedF11AndOwnExtraInformationNeverTrigger()
    {
        var emergency = new SelectiveInputEmergencyStop(new());
        var callbacks = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, new Context());
        callbacks.ConfigureEmergency(emergency);
        callbacks.ResetEmergency();
        emergency.SetActive(true);
        using var data = new Buffer();
        try
        {
            foreach (int modifier in new[] { 0xA2, 0xA4, 0xA0 })
            {
                Key(callbacks, data, modifier);
            }

            data.Keyboard(122, 0x10, 0);
            Assert.Equal(1, callbacks.Keyboard(0, 0x100, data.Pointer));
            Assert.False(emergency.IsRequested);
            data.Keyboard(122, 0, 1);
            Assert.Equal(123, callbacks.Keyboard(0, 0x100, data.Pointer));
            Assert.False(emergency.IsRequested);
            Assert.Equal(1, Key(callbacks, data, 122));
            Assert.True(emergency.IsRequested);
        }
        finally
        {
            await emergency.DisposeExecutionAsync();
        }
    }

    private static nint Key(SelectiveInputHookCallbacks callbacks, Buffer data,
        int key, bool down = true)
    {
        data.Keyboard((uint)key, 0, 0);
        return callbacks.Keyboard(0, down ? 0x100u : 0x101u, data.Pointer);
    }

    private sealed class Context : ISelectiveInputHookContext
    {
        internal int Queries;
        internal int Snapshots;
        public nint Foreground()
        {
            Queries++;
            return 7;
        }
        public nint ResolvePoint(int x, int y)
        {
            Queries++;
            return 7;
        }
        public nint Next(int code, nuint message, nint data) => 123;
        public IReadOnlyList<int> PressedEmergencyKeys()
        {
            Snapshots++;
            return [];
        }
    }

    private sealed class Buffer : IDisposable
    {
        internal nint Pointer { get; } = Marshal.AllocHGlobal(64);
        internal void Keyboard(uint key, uint flags, nuint extra) => Marshal.StructureToPtr(
            new KeyboardData { Key = key, Flags = flags, Extra = extra }, Pointer, false);
        internal void Mouse() => Marshal.StructureToPtr(new MouseData(), Pointer, false);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint Key;
        internal uint Scan;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        internal Native.Point Point;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }
}
