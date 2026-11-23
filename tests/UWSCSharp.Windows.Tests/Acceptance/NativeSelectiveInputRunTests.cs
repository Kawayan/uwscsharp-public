using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputRunTests
{
    [Fact]
    public async Task ExpiredRollbackSuspendsCallbacksWithoutRetryingRemoval()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ =>
        {
        });
        var clock = new Clock();
        var backend = new Backend
        {
            FailMouse = true,
            FailedRemovals = 1,
            OnRemove = () => clock.Timestamp = 5000
        };
        fixture.Invoke(() =>
        {
            var session = new NativeSelectiveInputHookSession(0,
                SelectiveInputDevices.All, backend, new Context());
            nint data = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.Copy(new byte[32], 0, data, 32);
                Marshal.WriteInt32(data, 0x87);
                Assert.ThrowsAny<Exception>(session.Start);
                Assert.Equal(1, backend.Removals);
                SelectiveInputStopResult result = SelectiveInputHookStop.Run(session, clock, 0);
                Assert.False(result.Stopped);
                Assert.Equal(1, backend.Removals);
                Assert.Equal(1, backend.Active);
                Assert.Equal(17, backend.Keyboard!(0, 0x100, data));
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                backend.FailedRemovals = 0;
                session.Stop();
                Assert.Equal(0, backend.Active);
            }
        });
    }

    [Fact]
    public async Task PublicSharedIdReplacementReleaseAndRootCancellationRemoveRealHooks()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ =>
        {
        });
        var backend = new Backend();
        await using IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信を使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (target, devices) =>
            {
                backend.Targets.Enqueue(target.Handle);
                return new NativeSelectiveInputHookSession(target, devices, backend);
            }).CreateSession();
        await using IWindowsAutomationSession child = root.Fork();
        using var cancel = new CancellationTokenSource();
        root.ConfigurePause(null, cancel.Token);
        Value id = await child.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(ValueKind.Empty, (await child.InvokeAsync(BuiltinFunctionNames.LockHardEx,
            [id, Value.Numeric(1)], default)).Kind);
        Assert.Equal(1, backend.Active);
        Assert.Equal(ValueKind.Empty, (await child.InvokeAsync(BuiltinFunctionNames.LockHardEx,
            [id, Value.Numeric(1)], default)).Kind);
        Assert.Equal(1, backend.Installs);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            child.InvokeAsync(BuiltinFunctionNames.LockHardEx,
                [Value.Numeric(int.MaxValue), Value.Numeric(2)], default).AsTask());
        Assert.Equal(0, backend.Removals);
        await child.InvokeAsync(BuiltinFunctionNames.LockHardEx, [id, Value.Numeric(2)], default);
        Assert.Equal(2, backend.Active);
        Assert.Equal(1, backend.Removals);
        await root.InvokeAsync(BuiltinFunctionNames.LockHardEx, [], default);
        Assert.Equal(0, backend.Active);
        await root.InvokeAsync(BuiltinFunctionNames.LockHardEx, [Value.Numeric(0)], default);
        Assert.Equal(2, backend.Active);
        cancel.Cancel();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Active) == 0, 5000));
        await child.DisposeAsync();
        await root.DisposeAsync();
        Assert.Equal(5, backend.Installs);
        Assert.Equal(5, backend.Removals);
        Assert.Single(backend.Threads.Distinct());
        Assert.All(backend.Targets.Take(2), handle => Assert.Equal(fixture.Root, handle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialRegistrationCleanupCountsInitialRollback(bool shared)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ =>
        {
        });
        var backend = new Backend { FailMouse = true, FailedRemovals = 2 };
        var target = SelectiveInputTarget.Capture(1, _ => fixture.Root,
            new NativeWindowIdentityPlatform());
        SelectiveInputHookOwner? owner = null;
        SelectiveInputRunOwner? run = null;
        try
        {
            if (shared)
            {
                run = new SelectiveInputRunOwner((selection, devices) =>
                    new NativeSelectiveInputHookSession(selection, devices, backend));
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    run.ChangeAsync(target, SelectiveInputDevices.All, default));
            }
            else
            {
                owner = new SelectiveInputHookOwner(() =>
                    new NativeSelectiveInputHookSession(target, SelectiveInputDevices.All, backend));
                await Assert.ThrowsAnyAsync<Exception>(() => owner.Ready);
                Assert.True(SpinWait.SpinUntil(() => backend.Removals >= 2, 5000));
                // 自動回収完了後も、登録と所有スレッドが保持されることを確認します。
                await Task.Delay(150);
            }

            Assert.Equal(2, backend.Removals);
            Assert.Equal(1, backend.Active);
            backend.FailedRemovals = 0;
            if (run is not null)
            {
                await run.ChangeAsync(null, SelectiveInputDevices.None, default);
            }
            else
            {
                await Assert.ThrowsAnyAsync<Exception>(() => owner!.DisposeAsync().AsTask());
            }

            Assert.Equal(3, backend.Removals);
            Assert.Equal(0, backend.Active);
        }
        finally
        {
            backend.FailedRemovals = 0;
            try
            {
                if (run is not null)
                {
                    await run.DisposeAsync();
                }
                else if (owner is not null)
                {
                    await owner.DisposeAsync();
                }
            }
            catch when (backend.Active == 0)
            {
                // 保持済みの失敗は上で検査し、実登録の回収を必須とします。
            }
        }
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal readonly ConcurrentQueue<nint> Targets = new();
        internal int Installs;
        internal int Removals;
        internal int Active;
        internal int FailedRemovals;
        internal bool FailMouse;
        internal Action? OnRemove;
        internal SelectiveInputHookProcedure? Keyboard;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (type == 13)
            {
                Keyboard = procedure;
            }

            if (FailMouse && type == 14)
            {
                return (0, 5);
            }

            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Installs);
                Interlocked.Increment(ref Active);
            }

            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref Removals);
            OnRemove?.Invoke();
            if (Interlocked.Decrement(ref FailedRemovals) >= 0)
            {
                return (false, 5);
            }

            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
            }

            return (success, error);
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal long Timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Timestamp;
    }

    private sealed class Context : ISelectiveInputHookContext
    {
        public nint Foreground() => 0;

        public nint ResolvePoint(int x, int y) => 0;

        public nint Next(int code, nuint message, nint data) => 17;
    }
}
