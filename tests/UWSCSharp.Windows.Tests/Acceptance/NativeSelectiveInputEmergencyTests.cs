using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputEmergencyTests
{
    [Fact]
    public async Task ConnectedCleanupConsumesDelayedRepeatAndReleaseBeforeRemovingTheHook()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var backend = new Backend();
        NativeSelectiveInputHookSession? session = null;
        var frames = new ConcurrentQueue<(bool Registered, nint Result)>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<SelectiveInputStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayed = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(window =>
            window.ObserveInput(window.Root, (message, key, _) =>
            {
                if (Volatile.Read(ref delayed) == 0 || key != 122 || message is not (0x100 or 0x101))
                {
                    return;
                }

                using var data = new Buffer();
                data.Set(122);
                bool registered = session?.IsRegistered == true;
                nint result = backend.Callbacks.TryGetValue(13, out SelectiveInputHookProcedure? keyboard)
                    ? keyboard(0, message, data.Pointer) : 0;
                frames.Enqueue((registered, result));
                if (message == 0x101)
                {
                    received.TrySetResult();
                }
            }));
        var emergency = new SelectiveInputEmergencyStop(new());
        emergency.Attach(() =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    fixture.Invoke(() =>
                    {
                        started.TrySetResult();
                        recovered.TrySetResult(SelectiveInputHookStop.Run(session!,
                            TimeProvider.System, TimeProvider.System.GetTimestamp()));
                    });
                }
                catch (Exception error) { recovered.TrySetException(error); }
            });
            return recovered.Task;
        });
        emergency.SetActive(true);
        try
        {
            fixture.Invoke(() =>
            {
                session = new NativeSelectiveInputHookSession(0,
                    SelectiveInputDevices.Mouse, backend, new Context());
                session.ConfigureEmergency(emergency);
                session.Start();
                using var data = new Buffer();
                foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
                {
                    Key(backend.Callbacks[13], data, key);
                }
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref delayed, 1);
            Assert.True(PostMessageW(fixture.Root, 0x100, 122, 0));
            Assert.True(PostMessageW(fixture.Root, 0x101, 122, 0));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            SelectiveInputStopResult result = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Stopped);
            Assert.Null(result.Error);
            Assert.Equal(2, frames.Count);
            Assert.All(frames, frame => { Assert.True(frame.Registered); Assert.Equal(1, frame.Result); });
            Assert.False(session!.IsRegistered);
        }
        finally
        {
            fixture.Invoke(() =>
            {
                if (backend.Callbacks.TryGetValue(13, out SelectiveInputHookProcedure? keyboard))
                {
                    using var data = new Buffer();
                    Key(keyboard, data, 122, false);
                }
                session?.Stop();
            });
            await emergency.DisposeExecutionAsync();
        }
    }

    [Fact]
    public async Task ExpiredReleaseBudgetKeepsTheMonitorUntilExplicitRecovery()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var emergency = new SelectiveInputEmergencyStop(new());
        emergency.SetActive(true);
        try
        {
            fixture.Invoke(() =>
            {
                var backend = new Backend();
                var session = new NativeSelectiveInputHookSession(0, SelectiveInputDevices.Mouse,
                    backend, new Context());
                session.ConfigureEmergency(emergency);
                using var data = new Buffer();
                try
                {
                    session.Start();
                    foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
                    {
                        Key(backend.Callbacks[13], data, key);
                    }
                    var clock = new ExpiredClock();
                    SelectiveInputStopResult expired = SelectiveInputHookStop.Run(session, clock, 0);
                    Assert.False(expired.Stopped);
                    Assert.IsType<TimeoutException>(expired.Error);
                    Assert.True(session.IsRegistered);
                    Assert.Equal(0, backend.Removals);
                    Assert.Equal(1, Key(backend.Callbacks[13], data, 122, false));
                    SelectiveInputStopResult recovered = SelectiveInputHookStop.Run(session, clock, clock.GetTimestamp());
                    Assert.True(recovered.Stopped);
                    Assert.Null(recovered.Error);
                }
                finally
                {
                    if (backend.Callbacks.TryGetValue(13, out SelectiveInputHookProcedure? keyboard))
                    {
                        Key(keyboard, data, 122, false);
                    }
                    session.Stop();
                }
            });
        }
        finally { await emergency.DisposeExecutionAsync(); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task EveryModeInstallsKeyboardMonitoringAndConsumesTheFakeChord(
        int selection, bool releasedDuringSnapshot)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var emergency = new SelectiveInputEmergencyStop(new());
        emergency.SetActive(true);
        try
        {
            fixture.Invoke(() =>
            {
                var backend = new Backend();
                var context = new Context { Pressed = [0xA2] };
                using var data = new Buffer();
                context.BeforeSnapshot = () =>
                {
                    Assert.Contains(13, backend.Callbacks.Keys);
                    if (releasedDuringSnapshot)
                    {
                        Key(backend.Callbacks[13], data, 0xA2, false);
                    }
                };
                var session = new NativeSelectiveInputHookSession(0,
                    (SelectiveInputDevices)selection, backend, context);
                session.ConfigureEmergency(emergency);
                try
                {
                    session.Start();
                    Assert.Contains(13, backend.Callbacks.Keys);
                    Assert.Equal((selection & 2) != 0, backend.Callbacks.ContainsKey(14));
                    SelectiveInputHookProcedure keyboard = backend.Callbacks[13];
                    Assert.Equal(selection == 2 ? 123 : 1, Key(keyboard, data, 65));
                    if (!releasedDuringSnapshot)
                    {
                        Key(keyboard, data, 0xA4);
                        Key(keyboard, data, 0xA0);
                        Assert.Equal(selection == 2 ? 123 : 1,
                            Key(keyboard, data, 122));
                        Assert.False(emergency.IsRequested);
                        Key(keyboard, data, 122, false);
                        Key(keyboard, data, 0xA2, false);
                        Key(keyboard, data, 0xA4, false);
                        Key(keyboard, data, 0xA0, false);
                    }

                    foreach (int key in new[] { 0xA2, 0xA4, 0xA0 })
                    {
                        Key(keyboard, data, key);
                    }

                    Assert.Equal(1, Key(keyboard, data, 122));
                    Assert.True(emergency.IsRequested);
                    Assert.True(session.IsRegistered);
                    Assert.Equal(123, Key(keyboard, data, 0xA2, false));
                    Assert.Equal(1, Key(keyboard, data, 122, false));
                    Assert.Equal(123, Key(keyboard, data, 65));
                    Assert.Equal(1, context.Snapshots);
                }
                finally
                {
                    session.Stop();
                    Assert.False(session.IsRegistered);
                }
            });
        }
        finally
        {
            await emergency.DisposeExecutionAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RealInjectedF11CannotTriggerWithFakePhysicalModifiers(int selection)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        var emergency = new SelectiveInputEmergencyStop(new());
        var backend = new ObservingBackend();
        var context = new Context
        {
            Native = new NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree.Capture())
        };
        NativeSelectiveInputHookSession? session = null;
        emergency.SetActive(true);
        try
        {
            fixture.Invoke(() =>
            {
                session = new NativeSelectiveInputHookSession(0,
                    (SelectiveInputDevices)selection, backend, context);
                session.ConfigureEmergency(emergency);
                session.Start();
                Assert.Empty(context.LastPressed);
                context.Simulating = true;
                try
                {
                    // 判定器だけへ偽の物理修飾子を渡し、OSや次のフックへは転送しません。
                    using var data = new Buffer();
                    foreach (int key in new[] { 0xA2, 0xA4, 0xA0 })
                    {
                        Key(backend.Keyboard!, data, key);
                    }
                }
                finally
                {
                    context.Simulating = false;
                }
            });
            Assert.Equal(fixture.Root, Native.GetForegroundWindow());
            Native.Input[] inputs = [Input(122, false, 0), Input(122, true, 0),
                Input(122, false, 1), Input(122, true, 1)];
            Assert.Equal((uint)inputs.Length,
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()));
            await backend.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Invoke(() => session!.Pump());
            Assert.Equal(4, backend.Events.Count);
            Assert.All(backend.Events, item => Assert.NotEqual(0u, item.Flags & 0x12));
            Assert.Equal(new nuint[] { 0, 0, 1, 1 }, backend.Events.Select(item => item.Extra));
            Assert.False(emergency.IsRequested);
            Assert.False(emergency.Token.IsCancellationRequested);
        }
        finally
        {
            fixture.Invoke(() => session?.Stop());
            await emergency.DisposeExecutionAsync();
        }
    }

    [Fact]
    public async Task FailedInitialSnapshotStillAllowsOneCleanupRetry()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var emergency = new SelectiveInputEmergencyStop(new());
        try
        {
            fixture.Invoke(() =>
            {
                var error = new InvalidOperationException("検証用の初期状態取得失敗");
                var backend = new Backend { FailedRemovals = 1 };
                var context = new Context { BeforeSnapshot = () => throw error };
                var session = new NativeSelectiveInputHookSession(0, SelectiveInputDevices.Mouse,
                    backend, context);
                session.ConfigureEmergency(emergency);
                try
                {
                    Assert.Contains(error, Assert.Throws<AggregateException>(session.Start).InnerExceptions);
                    SelectiveInputStopResult result = SelectiveInputHookStop.Run(session,
                        TimeProvider.System, TimeProvider.System.GetTimestamp());
                    Assert.True(result.Stopped);
                    Assert.Null(result.Error);
                    Assert.Equal(2, backend.Removals);
                    Assert.False(session.IsRegistered);
                }
                finally
                {
                    backend.FailedRemovals = 0;
                    session.Stop();
                }
            });
        }
        finally
        {
            await emergency.DisposeExecutionAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPartialKeyboardCanRequestRecoveryAfterInitializationFailure(bool snapshotFailure)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var emergency = new SelectiveInputEmergencyStop(new());
        var recovered = new TaskCompletionSource<SelectiveInputStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeSelectiveInputHookSession? session = null;
        var backend = new Backend { FailedInstall = snapshotFailure ? 0 : 14, FailedRemovals = 10 };
        int snapshots = 0;
        var context = new Context
        {
            BeforeSnapshot = () =>
        {
            if (snapshotFailure && ++snapshots <= 2)
            {
                throw new InvalidOperationException("検証用の初期状態取得失敗");
            }
        }
        };
        emergency.Attach(() => Task.Run(() => fixture.Invoke(() =>
            recovered.TrySetResult(SelectiveInputHookStop.Run(session!, TimeProvider.System, TimeProvider.System.GetTimestamp())))));
        emergency.SetRetained(true);
        try
        {
            fixture.Invoke(() =>
            {
                session = new NativeSelectiveInputHookSession(0, SelectiveInputDevices.Mouse, backend, context);
                session.ConfigureEmergency(emergency);
                Assert.Throws<AggregateException>(session.Start);
                SelectiveInputStopResult failed = SelectiveInputHookStop.Run(session, TimeProvider.System, TimeProvider.System.GetTimestamp());
                Assert.False(failed.Stopped);
                Assert.True(session.IsRegistered);
                session.Pump();
                backend.FailedRemovals = 0;
                using var data = new Buffer();
                foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
                {
                    Key(backend.Callbacks[13], data, key, false);
                }
                foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
                {
                    Key(backend.Callbacks[13], data, key);
                }
                Assert.True(emergency.IsRequested);
                Assert.Equal(1, Key(backend.Callbacks[13], data, 122, false));
            });
            Assert.True((await recovered.Task.WaitAsync(TimeSpan.FromSeconds(8))).Stopped);
        }
        finally
        {
            fixture.Invoke(() => { backend.FailedRemovals = 0; session?.Stop(); });
            await emergency.DisposeExecutionAsync();
        }
    }

    private static nint Key(SelectiveInputHookProcedure callback, Buffer data,
        int key, bool down = true)
    {
        data.Set((uint)key);
        return callback(0, down ? 0x100u : 0x101u, data.Pointer);
    }

    private static Native.Input Input(ushort key, bool up, nuint extra) => new()
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = key, Flags = up ? 2u : 0u, Extra = extra }
        }
    };

    private sealed class Context : ISelectiveInputHookContext
    {
        internal int[] Pressed = [];
        internal Action? BeforeSnapshot;
        internal int Snapshots;
        internal IReadOnlyList<int> LastPressed = [];
        internal bool Simulating;
        internal NativeSelectiveInputHookContext? Native;
        public nint Foreground() => 0;
        public nint ResolvePoint(int x, int y) => 0;
        public nint Next(int code, nuint message, nint data) =>
            Simulating || Native is null ? 123 : Native.Next(code, message, data);
        public IReadOnlyList<int> PressedEmergencyKeys()
        {
            BeforeSnapshot?.Invoke();
            Snapshots++;
            LastPressed = Native?.PressedEmergencyKeys() ?? Pressed;
            return LastPressed;
        }
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        internal readonly Dictionary<int, SelectiveInputHookProcedure> Callbacks = [];
        internal int FailedInstall;
        internal int FailedRemovals;
        internal int Removals;
        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            if (type == FailedInstall)
            {
                return (0, 5);
            }
            Callbacks.Add(type, procedure);
            return (type, 0);
        }
        public (bool Success, int Error) Remove(nint hook)
        {
            Removals++;
            if (FailedRemovals-- > 0)
            {
                return (false, 5);
            }
            Callbacks.Remove((int)hook);
            return (true, 0);
        }
    }

    private sealed class ObservingBackend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        private readonly Dictionary<nint, SelectiveInputHookProcedure> roots = [];
        internal SelectiveInputHookProcedure? Keyboard;
        internal readonly ConcurrentQueue<(uint Flags, nuint Extra)> Events = new();
        internal readonly TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            SelectiveInputHookProcedure wrapper = procedure;
            if (type == 13)
            {
                Keyboard = procedure;
                wrapper = (code, message, data) =>
                {
                    if (code == 0 && Marshal.PtrToStructure<KeyboardData>(data) is { Key: 122 } key)
                    {
                        Events.Enqueue((key.Flags, key.Extra));
                        if (Events.Count == 4)
                        {
                            Received.TrySetResult();
                        }
                    }
                    return procedure(code, message, data);
                };
            }
            (nint handle, int error) = native.Install(type, wrapper);
            if (handle != 0)
            {
                roots.Add(handle, wrapper);
            }
            return (handle, error);
        }
        public (bool Success, int Error) Remove(nint hook)
        {
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                roots.Remove(hook);
            }
            return (success, error);
        }
    }

    private sealed class Buffer : IDisposable
    {
        internal nint Pointer { get; } = Marshal.AllocHGlobal(Marshal.SizeOf<KeyboardData>());
        internal void Set(uint key) => Marshal.StructureToPtr(new KeyboardData { Key = key }, Pointer, false);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private sealed class ExpiredClock : TimeProvider
    {
        public override long GetTimestamp() => 100;
        public override long TimestampFrequency => 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint key, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint Key;
        internal uint Scan;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }
}
