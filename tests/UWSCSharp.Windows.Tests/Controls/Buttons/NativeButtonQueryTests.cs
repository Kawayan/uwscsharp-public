using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, -1)]
    public async Task QuerySkipsDisabledButtonsBeforeApplyingNumber(int number, int expected)
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint first = host.Child("BUTTON", "same", 6 | 0x08000000, 10);
            nint second = host.Child("BUTTON", "same", 6, 40);
            Native.SendMessageW(first, 0xf1, 1, 0);
            Native.SendMessageW(second, 0xf1, 2, 0);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
            throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: new Platform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN",
            [id, Value.String("same"), Value.Numeric(number)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryChecksEnabledAfterSecondGate(bool initiallyEnabled)
    {
        nint button = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
            button = host.Child("BUTTON", "target",
                6 | (initiallyEnabled ? 0 : 0x08000000), 10));
        var accessible = new Accessible
        {
            Gating = count =>
            {
                if (count == 2)
                {
                    EnableWindow(button, !initiallyEnabled);
                }
            }
        };
        accessible.Marker.Enqueue(true);
        accessible.Marker.Enqueue(false);
        accessible.States.Enqueue((2, 44, true));
        var platform = new Platform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform,
            buttonAccessibleFactory: _ => accessible).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target")], default));
        Assert.Equal(initiallyEnabled ? 2 : 0, accessible.Gates);
        Assert.Equal(initiallyEnabled ? 0 : 1, accessible.States.Count);
        Assert.Equal(0, accessible.Actions);
        Assert.Empty(platform.Posts);
    }

    [Fact]
    public async Task QueryCancellationAtSecondGatePreventsReturn()
    {
        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "target", 6, 10);
        using var cancellation = new CancellationTokenSource();
        var accessible = new Accessible
        {
            Gating = count =>
            {
                if (count == 2)
                {
                    cancellation.Cancel();
                }
            }
        };
        accessible.States.Enqueue((1, 44, false));
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: new Platform(),
            buttonAccessibleFactory: _ => accessible).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CHKBTN", [id, Value.String("target")], cancellation.Token));
        Assert.Equal(2, accessible.Gates);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
}
