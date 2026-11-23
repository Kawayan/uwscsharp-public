using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class KeyboardHotkeyGuardTests
{
    [Theory]
    [InlineData(120, 120)]
    [InlineData(161, 16)]
    [InlineData(163, 17)]
    [InlineData(165, 18)]
    [InlineData(901, 13)]
    public async Task OnlyPhysicalDownUsesTheNormalizedHotkeyGuard(int raw, int normalized)
    {
        var inputs = new List<Native.Input>();
        var guarded = new List<int>();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add, keyboardInputGuard: (key, send) =>
            {
                guarded.Add(key);
                send();
            }).CreateSession();
        await session.InvokeAsync(BuiltinFunctionNames.Kbd, [Value.Numeric(raw)], default);
        Assert.Equal([normalized], guarded);
        Assert.Equal(2, inputs.Count);
        await session.InvokeAsync(BuiltinFunctionNames.Kbd, [Value.Numeric(0x3042)], default);
        Assert.Single(guarded);
        Assert.Equal(4, inputs.Count);
    }

    [Fact]
    public async Task SentDownRemainsTrackedWhenHotkeyRestorationFails()
    {
        var inputs = new List<Native.Input>();
        var failure = new InvalidOperationException("再登録失敗");
        IWindowsAutomationSession session = new WindowsAutomation(inputs.Add,
            keyboardInputGuard: (_, send) =>
            {
                send();
                throw failure;
            }).CreateSession();
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Kbd, [Value.Numeric(120)], default));
        Assert.Same(failure, error);
        Assert.Single(inputs);
        await session.DisposeAsync();
        Assert.Equal([0u, 2u], inputs.Select(input => input.Data.Keyboard.Flags));
    }

    [Fact]
    public async Task FailedGuardDoesNotInventHeldInput()
    {
        var inputs = new List<Native.Input>();
        IWindowsAutomationSession session = new WindowsAutomation(inputs.Add,
            keyboardInputGuard: (_, _) => throw new IOException("解除失敗")).CreateSession();
        await Assert.ThrowsAsync<IOException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Kbd, [Value.Numeric(120)], default));
        await session.DisposeAsync();
        Assert.Empty(inputs);
    }

    [Fact]
    public async Task CancellationWhileEnteringTheGuardSendsNoInput()
    {
        using var cancellation = new CancellationTokenSource();
        var inputs = new List<Native.Input>();
        await using IWindowsAutomationSession session = new WindowsAutomation(inputs.Add,
            keyboardInputGuard: (_, send) =>
            {
                cancellation.Cancel();
                send();
            }).CreateSession();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.InvokeAsync(BuiltinFunctionNames.Kbd,
                [Value.Numeric(120)], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(inputs);
    }
}
