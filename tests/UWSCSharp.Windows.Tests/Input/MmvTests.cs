using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MmvTests
{
    [Theory]
    [InlineData(30, 40)]
    [InlineData(-30, -40)]
    [InlineData(65539, 65540)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void DirectMovementUsesRootAndSignedWordCoordinates(int x, int y)
    {
        var backend = new DirectInputTests.Backend();
        var session = new DirectInputSession(backend);
        session.Move(10, new(x, y), default);
        (nint Window, uint Message, uint WParam, uint LParam) message =
            Assert.Single(backend.Messages);
        Assert.Equal((nint)10, message.Window);
        Assert.Equal(0x200u, message.Message);
        Assert.Equal(0u, message.WParam);
        Assert.Equal(unchecked((ushort)x), (ushort)message.LParam);
        Assert.Equal(unchecked((ushort)y), (ushort)(message.LParam >> 16));
        Assert.Empty(backend.Modifiers);
    }

    [Fact]
    public void DestroyedDirectTargetDoesNotReceiveMovement()
    {
        var backend = new DirectInputTests.Backend { InvalidWindow = 10 };
        new DirectInputSession(backend).Move(10, new(30, 40), default);
        Assert.Empty(backend.Messages);
    }

    [Fact]
    public async Task MovementPreservesHeldButtonStateAndRelease()
    {
        var backend = new DirectInputTests.Backend();
        var session = new DirectInputSession(backend);
        await session.MouseAsync(10, 0, 1, new(30, 40), default);
        backend.Messages.Clear();
        session.Move(10, new(50, 60), default);
        Assert.Equal(1u, Assert.Single(backend.Messages).WParam);
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Empty(errors);
        Assert.Equal(0x202u, backend.Messages[^1].Message);
    }

    [Fact]
    public async Task CancelledDelayNeverSendsMovement()
    {
        var backend = new DirectInputTests.Backend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            backend).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [Value.Numeric(10), Value.Numeric(2)], default);
        using var stop = new CancellationTokenSource();
        Task<Value> movement = session.InvokeAsync(BuiltinFunctionNames.Mmv,
            [Value.Numeric(30), Value.Numeric(40), Value.Numeric(10000)],
            stop.Token).AsTask();
        Assert.False(movement.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => movement);
        Assert.Empty(backend.Messages);
    }

    [Fact]
    public async Task PausedMovementCanBeStoppedWithoutSending()
    {
        var backend = new DirectInputTests.Backend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            backend).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [Value.Numeric(10), Value.Numeric(2)], default);
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource();
        session.ConfigurePause(control, stop.Token);
        control.Pause();
        Task<Value> movement = session.InvokeAsync(BuiltinFunctionNames.Mmv,
            [Value.Numeric(30), Value.Numeric(40), Value.Numeric(10000)],
            stop.Token).AsTask();
        Assert.True(control.IsPaused);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => movement);
        Assert.Empty(backend.Messages);
    }
}
