using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputWindowLifetimeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RegistrationIdentityMustBeValidBeforeFirstLock(int change)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ =>
            {
            });
        var platform = new Platform { Missing = change == 3 };
        int starts = 0;
        await using IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力を送信しません。"),
            swappedMouseButtons: false,
            selectiveInputFactory: (_, _) => new Session(() => Interlocked.Increment(ref starts)),
            selectiveInputPlatform: platform).CreateSession();
        Value id = await root.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        platform.Missing = false;
        platform.Change = change;
        ISelectiveInputOwnershipSession ownership = Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(root);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ownership.SetSelectiveInputAsync(new((int)id.Number, 1), default));
        Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
        Assert.Equal(0, Volatile.Read(ref starts));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkerDiscoveryCreatesNewIdWithoutRevivingOldRegistration(int change)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ =>
            {
            });
        var platform = new Platform();
        Session? hook = null;
        await using IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力を送信しません。"),
            swappedMouseButtons: false,
            selectiveInputFactory: (_, _) => hook = new Session(() =>
            {
            }),
            selectiveInputPlatform: platform).CreateSession();
        Value oldId = await root.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        await using IWindowsAutomationSession worker = root.Fork();
        platform.Change = change;
        Value nextId = await worker.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Assert.NotEqual(oldId.Number, nextId.Number);
        ISelectiveInputOwnershipSession ownership = Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ownership.SetSelectiveInputAsync(new((int)oldId.Number, 1), default));
        await ownership.SetSelectiveInputAsync(new((int)nextId.Number, 1), default);
        Assert.NotNull(hook);
        Assert.True(hook.IsRegistered);
        await worker.DisposeAsync();
        Assert.True(hook.IsRegistered);
        await ownership.SetSelectiveInputAsync(new(null, 0), default);
        Assert.False(hook.IsRegistered);
    }

    // 所有ウィンドウは実体を使い、PID/TIDの変化と照会不能だけを注入します。
    private sealed class Platform : IWindowIdentityPlatform
    {
        private readonly NativeWindowIdentityPlatform native = new();
        internal int Change;
        internal bool Missing;

        public WindowIdentity? ReadIdentity(nint window)
        {
            if (Missing)
            {
                return null;
            }

            WindowIdentity? identity = native.ReadIdentity(window);
            return identity is { } value ? Change switch
            {
                1 => value with { ProcessId = value.ProcessId + 1 },
                2 => value with { ThreadId = value.ThreadId + 1 },
                _ => value
            } : null;
        }
    }

    // OSフックは登録せず、対象検証が所有セッションの開始より前に行われるかを確認します。
    private sealed class Session(Action started) : ISelectiveInputHookSession
    {
        public bool IsRegistered
        {
            get; private set;
        }

        public void Start()
        {
            IsRegistered = true;
            started();
        }

        public bool Pump() => false;

        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);

        public void Stop() => IsRegistered = false;

        public void RefreshTargets()
        {
        }
    }
}
