using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowsInputTests
{
    private static Value N(int n) => Value.Numeric(n);

    [Theory]
    [InlineData(65, 65, 0)]
    [InlineData(0xa1, 16, 1)]
    [InlineData(0xa3, 17, 1)]
    [InlineData(901, 13, 1)]
    [InlineData(0x3042, 0, 4)]
    [InlineData(0xd800, 0, 4)]
    [InlineData(0xffff, 0, 4)]
    public async Task KeyboardClickProducesDownAndUp(
        int key, int vk, uint flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inputs = new List<Native.Input>();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add).CreateSession();
        await session.InvokeAsync("KBD", [N(key)], default);
        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, input => Assert.Equal(1u,
            input.Type));
        Assert.Equal((ushort)vk, inputs[0].Data.Keyboard.Vk);
        Assert.Equal(flags, inputs[0].Data.Keyboard.Flags);
        Assert.Equal(flags | 2, inputs[1].Data.Keyboard.Flags);
        Assert.Equal((nuint)1, inputs[0].Data.Keyboard.Extra);
        if (vk == 0)
        {
            Assert.Equal((ushort)key,
                inputs[0].Data.Keyboard.Scan);
        }
        else
        {
            Assert.Equal((ushort)Native.MapVirtualKeyW(
                (uint)key, 0), inputs[0].Data.Keyboard.Scan);
        }

        Assert.Equal(IntPtr.Size == 8 ? 40 : 28,
            Marshal.SizeOf<Native.Input>());
    }

    [Theory]
    [InlineData(0, 2, 4)]
    [InlineData(1, 8, 16)]
    [InlineData(2, 32, 64)]
    public async Task MouseClickProducesOrderedButtonEvents(
        int button, uint down, uint up)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Native.Input> inputs = [];
        await using IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add).CreateSession();
        await session.InvokeAsync("BTN", [N(button)], default);
        Assert.Equal([down, up], inputs.Select(
            input => input.Data.Mouse.Flags));
    }

    [Fact]
    public async Task WheelUsesSignedNotchesAndDoesNotLeaveHeldInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Native.Input> inputs = [];
        IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add).CreateSession();
        await session.InvokeAsync("BTN", [N(5), N(-2)], default);
        await session.DisposeAsync();
        Native.Input input = Assert.Single(inputs);
        Assert.Equal(0x800u, input.Data.Mouse.Flags);
        Assert.Equal(unchecked((uint)-240),
            input.Data.Mouse.Data);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task DisposalReleasesHeldInputsOnceAndTriesAllReleases()
    {
        // 全入力をfake senderで受け、画面・実入力・focusを操作しません。
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Native.Input> inputs = [];
        bool fail = false;
        IWindowsAutomationSession session = new WindowsAutomation(
            input =>
        {
            inputs.Add(input);
            if (fail && input.Type == 1)
            {
                throw new IOException("release failed");
            }
        }).CreateSession();
        await session.InvokeAsync("KBD", [N(17), N(1)], default);
        await session.InvokeAsync("KBD", [N(65), N(1)], default);
        await session.InvokeAsync("BTN", [N(0), N(1)], default);
        fail = true;
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            async () => await session.DisposeAsync());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(6, inputs.Count);
        Assert.Equal(4u, inputs[^1].Data.Mouse.Flags);
        await session.DisposeAsync();
        Assert.Equal(6, inputs.Count);
    }

    [Fact]
    public async Task CancellationBetweenMouseDownAndUpStillReleasesInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Native.Input> inputs = [];
        using var cancellation = new CancellationTokenSource();
        IWindowsAutomationSession session = new WindowsAutomation(
            input =>
        {
            inputs.Add(input);
            cancellation.Cancel();
        }).CreateSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.InvokeAsync("BTN", [N(0)],
            cancellation.Token));
        await session.DisposeAsync();
        Assert.Equal([2u, 4u], inputs.Select(
            input => input.Data.Mouse.Flags));
    }

    [Fact]
    public async Task ExplicitUpRemovesHeldInputAndFailedDownIsNotRecorded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<Native.Input> inputs = [];
        IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add).CreateSession();
        await session.InvokeAsync("KBD", [N(65), N(1)], default);
        await session.InvokeAsync("KBD", [N(65), N(2)], default);
        await session.DisposeAsync();
        Assert.Equal(2, inputs.Count);
        int attempts = 0;
        session = new WindowsAutomation(_ =>
        {
            attempts++;
            throw new IOException();
        }).CreateSession();
        await Assert.ThrowsAsync<IOException>(async () => await session.InvokeAsync("KBD", [N(65), N(1)], default));
        await session.DisposeAsync();
        Assert.Equal(1, attempts);
    }
}
