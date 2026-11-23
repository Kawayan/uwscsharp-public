using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputTargetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DestroyedTargetStopsAndRemovesOwnedHook()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var target = SelectiveInputTarget.Capture(17, _ => fixture.Root,
            new NativeWindowIdentityPlatform());
        var backend = new Backend();
        var owner = new SelectiveInputHookOwner(() =>
            new NativeSelectiveInputHookSession(target, SelectiveInputDevices.Keyboard, backend));
        try
        {
            await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref backend.Active));
            await fixture.DisposeAsync();
            await AssertStoppedAsync(owner, backend, 1);
            Assert.Equal(fixture.Root, target.Handle);
            Assert.Equal(17, target.WindowId);
        }
        finally
        {
            await FinishAsync(owner, backend);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChangedOwnerIdentityStopsBothHooks(int change)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var platform = new ChangingPlatform();
        var target = SelectiveInputTarget.Capture(17, _ => fixture.Root, platform);
        var backend = new Backend();
        var owner = new SelectiveInputHookOwner(() =>
            new NativeSelectiveInputHookSession(target, SelectiveInputDevices.All, backend));
        try
        {
            await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref backend.Active));
            Volatile.Write(ref platform.Change, change);
            await AssertStoppedAsync(owner, backend, 2);
            Assert.Equal(fixture.Root, target.Handle);
            Assert.Equal(17, target.WindowId);
        }
        finally
        {
            await FinishAsync(owner, backend);
        }
    }

    [Fact]
    public async Task ChangeBeforeOwnerFactoryDoesNotInstallHooks()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var platform = new ChangingPlatform();
        var target = SelectiveInputTarget.Capture(17, _ => fixture.Root, platform);
        var backend = new Backend();
        Volatile.Write(ref platform.Change, 1);
        var owner = new SelectiveInputHookOwner(() =>
            new NativeSelectiveInputHookSession(target, SelectiveInputDevices.All, backend));
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.Ready.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
            await AssertStoppedAsync(owner, backend, 0);
            Assert.Equal(0, backend.Installations);
        }
        finally
        {
            await FinishAsync(owner, backend);
        }
    }

    [Fact]
    public async Task ChangeAfterConstructionIsCheckedAgainBeforeRegistration()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var platform = new ChangingPlatform();
        var target = SelectiveInputTarget.Capture(17, _ => fixture.Root, platform);
        var backend = new Backend();
        fixture.Invoke(() =>
        {
            var session = new NativeSelectiveInputHookSession(target,
                SelectiveInputDevices.All, backend);
            try
            {
                Volatile.Write(ref platform.Change, 2);
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(session.Start);
                Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
                Assert.False(session.IsRegistered);
                Assert.Equal(0, backend.Installations);
            }
            finally
            {
                session.Stop();
            }
        });
    }

    private async Task AssertStoppedAsync(SelectiveInputHookOwner owner,
        Backend backend, int removals)
    {
        Assert.True(SpinWait.SpinUntil(() => !owner.IsAlive, 5000));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.DisposeAsync().AsTask());
        Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
        Assert.Equal(0, Volatile.Read(ref backend.Active));
        Assert.Equal(removals, Volatile.Read(ref backend.Removals));
        output.WriteLine($"installations={backend.Installations} removals={backend.Removals} active={backend.Active}");
    }

    private static async Task FinishAsync(SelectiveInputHookOwner owner, Backend backend)
    {
        try
        {
            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) when (!owner.IsAlive && backend.Active == 0)
        {
            // 検査済みの失効エラーを保持したまま、後始末で登録が残らないことを確認します。
        }
    }

    private sealed class ChangingPlatform : IWindowIdentityPlatform
    {
        private readonly NativeWindowIdentityPlatform native = new();
        internal int Change;

        public WindowIdentity? ReadIdentity(nint window)
        {
            WindowIdentity? identity = native.ReadIdentity(window);
            if (identity is not { } current)
            {
                return null;
            }

            // 所有者の不一致を注入し、実際のHWND再利用とは区別します。
            return Volatile.Read(ref Change) switch
            {
                1 => current with { ProcessId = unchecked(current.ProcessId + 1) },
                2 => current with { ThreadId = unchecked(current.ThreadId + 1) },
                _ => current
            };
        }
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal int Installations;
        internal int Removals;
        internal int Active;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Installations);
                Interlocked.Increment(ref Active);
            }

            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Interlocked.Increment(ref Removals);
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
            }

            return (success, error);
        }
    }
}
