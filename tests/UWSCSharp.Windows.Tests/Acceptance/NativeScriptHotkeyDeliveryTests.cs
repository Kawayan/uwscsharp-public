using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeScriptHotkeyDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PressNotifiesBeforeReleaseAndRemovalStopsDelivery(bool modifierOnly)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        await using var source = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
        ScriptHotkey key = modifierOnly ? new ScriptHotkey(0, 6) : new ScriptHotkey(65, 2);
        ushort[] keys = modifierOnly ? [0xA2, 0xA0] : [0xA2, 65];
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Assert.True(source.TryRegister(key, () =>
        {
            Interlocked.Increment(ref calls);
            received.TrySetResult();
        }));
        await SendAsync(() => received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(source.Failure);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(source.TryUnregister(key));
        await SendAsync(() => Task.Delay(300));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(fixture.Root, Native.GetForegroundWindow());

        async Task SendAsync(Func<Task> whilePressed)
        {
            try
            {
                foreach (ushort pressed in keys)
                {
                    Send(pressed, false);
                }

                await whilePressed();
            }
            finally
            {
                List<Exception> failures = [];
                foreach (ushort released in keys.Reverse())
                {
                    try
                    {
                        Send(released, true);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                if (failures.Count != 0)
                {
                    throw new AggregateException("検証用キーの解放に失敗しました。", failures);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModifierSidesAndRepeatedDownNotifyOnlyOnNewCombination(bool rightFirst)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        await using var source = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform());
        ushort first = rightFirst ? (ushort)0xA1 : (ushort)0xA0;
        ushort second = rightFirst ? (ushort)0xA0 : (ushort)0xA1;
        using var received = new SemaphoreSlim(0);
        int calls = 0;
        Assert.True(source.TryRegister(new ScriptHotkey(0, 4), () =>
        {
            Interlocked.Increment(ref calls);
            received.Release();
        }));
        try
        {
            Send(first, false);
            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
            Send(first, false);
            Send(first, false);
            Send(second, false);
            await AssertCountAsync(1);

            // 片側を解放しても、もう片側が保持中なら組合せは変わりません。
            Send(first, true);
            Send(second, false);
            await AssertCountAsync(1);
            Send(second, true);
            await AssertCountAsync(1);

            Send(first, false);
            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
            await AssertCountAsync(2);
            Assert.Null(source.Failure);
            Assert.Equal(fixture.Root, Native.GetForegroundWindow());
        }
        finally
        {
            try
            {
                Send(first, true);
            }
            finally
            {
                Send(second, true);
            }
        }

        async Task AssertCountAsync(int expected)
        {
            await Task.Delay(300);
            Assert.Equal(expected, Volatile.Read(ref calls));
        }
    }

    [Theory]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    public async Task ModifierAlreadyHeldAtRegistrationCompletesCombination(
        ushort control)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        await using var source = new WindowsScriptHotkeySource(
            new NativeScriptHotkeyPlatform());
        const ushort shift = 0xA0;
        var received = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(0, Native.GetAsyncKeyState(control) & 0x8000);
        Assert.Equal(0, Native.GetAsyncKeyState(shift) & 0x8000);
        try
        {
            Send(control, false);
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            while ((Native.GetAsyncKeyState(control) & 0x8000) == 0)
            {
                await Task.Delay(10, deadline.Token);
            }

            Assert.True(source.TryRegister(new ScriptHotkey(0, 6),
                () => received.TrySetResult()));
            Assert.False(received.Task.IsCompleted);
            Send(shift, false);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(source.Failure);
            Assert.Equal(fixture.Root, Native.GetForegroundWindow());
        }
        finally
        {
            try
            {
                Send(shift, true);
            }
            finally
            {
                Send(control, true);
            }
        }
    }

    [Fact]
    public Task NativePressDuringModifierSnapshotCompletesCombination() =>
        CheckNativeTransitionDuringSnapshotAsync(release: false);

    [Fact]
    public Task NativeReleaseDuringModifierSnapshotClearsSampledKey() =>
        CheckNativeTransitionDuringSnapshotAsync(release: true);

    private static async Task CheckNativeTransitionDuringSnapshotAsync(
        bool release)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(fixture.Root);
        var backend = new SnapshotTransitionBackend(release);
        await using var source = new WindowsScriptHotkeySource(
            new NativeScriptHotkeyPlatform(backend));
        const ushort shift = 0xA0;
        var received = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(0, Native.GetAsyncKeyState(shift) & 0x8000);
        Assert.Equal(0, Native.GetAsyncKeyState(
            SnapshotTransitionBackend.Control) & 0x8000);
        try
        {
            if (release)
            {
                Send(SnapshotTransitionBackend.Control, false);
                using var deadline = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                while ((Native.GetAsyncKeyState(
                    SnapshotTransitionBackend.Control) & 0x8000) == 0)
                {
                    await Task.Delay(10, deadline.Token);
                }
            }

            Assert.True(source.TryRegister(new ScriptHotkey(0, 6),
                () => received.TrySetResult()));
            Assert.True(backend.TransitionObserved);
            Assert.False(received.Task.IsCompleted);
            Send(shift, false);
            if (release)
            {
                await Task.Delay(300);
                Assert.False(received.Task.IsCompleted);
                Send(SnapshotTransitionBackend.Control, false);
            }

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(source.Failure);
            Assert.Equal(fixture.Root, Native.GetForegroundWindow());
        }
        finally
        {
            try
            {
                Send(shift, true);
            }
            finally
            {
                Send(SnapshotTransitionBackend.Control, true);
            }
        }
    }

    private sealed class SnapshotTransitionBackend(bool release)
        : IModifierHookBackend
    {
        internal const ushort Control = 0xA2;
        private readonly NativeModifierHookBackend native = new();
        private ModifierHookProcedure? observedProcedure;
        private bool sampling;

        internal bool TransitionObserved
        {
            get;
            private set;
        }

        public nint Install(ModifierHookProcedure procedure)
        {
            observedProcedure = (code, message, data) =>
            {
                nint result = procedure(code, message, data);
                if (sampling && code == 0
                    && message == (release ? 0x101u : 0x100u)
                    && Marshal.ReadInt32(data) == Control)
                {
                    TransitionObserved = true;
                }

                return result;
            };
            return native.Install(observedProcedure);
        }

        public bool Remove(nint hook)
        {
            bool removed = native.Remove(hook);
            GC.KeepAlive(observedProcedure);
            return removed;
        }

        public bool IsPressed(int key)
        {
            if (key == 0xA3)
            {
                // 左Ctrlの採取後、次のキーを読む間に実フックへ入力を届けます。
                sampling = true;
                try
                {
                    Send(Control, release);
                    long start = Stopwatch.GetTimestamp();
                    while (!TransitionObserved && Stopwatch.GetElapsedTime(start)
                        < TimeSpan.FromSeconds(5))
                    {
                        Native.PeekMessageW(out _, 0, 0, 0, 0);
                        Thread.Sleep(1);
                    }

                    Assert.True(TransitionObserved,
                        "初期採取中にWin32フックの通知が届きませんでした。");
                }
                finally
                {
                    sampling = false;
                }
            }

            return native.IsPressed(key);
        }

        public nint Next(int code, nuint message, nint data) =>
            native.Next(code, message, data);
    }

    private static void Send(ushort key, bool up) => NativeInputSender.Send(new Native.Input
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = key, Flags = up ? 2u : 0 }
        }
    });
}
