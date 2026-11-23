using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewExceptionTests
{
    private sealed class Platform(string stage) : OwnedListSelectionPlatform
    {
        internal int Restores;
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root, Buffers);

        public override bool IsVisible(nint window) => false;

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint window, CancellationToken token)
        {
            Assert.Equal(77, window);
            Assert.True(Buffers.Released);
            Assert.True(Buffers.Closed);
            Restores++;
            if (stage == "restore")
            {
                throw new InvalidOperationException("復元失敗");
            }
        }

        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (stage == "neighbor" && message == 0x100 && parameter == 40)
            {
                throw new InvalidOperationException("隣接通知失敗");
            }

            return 0;
        }

        public override Native.Point ToScreen(nint window, Native.Point point) => stage switch
        {
            "action" => throw new InvalidOperationException("座標変換失敗"),
            "cancellation" => throw new OperationCanceledException(),
            _ => point,
        };
    }

    [Theory]
    [InlineData("neighbor")]
    [InlineData("action")]
    [InlineData("cancellation")]
    [InlineData("restore")]
    public async Task OnlyActionFailureBecomesFalse(string stage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        var platform = new Platform(stage);
        int points = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, _, pointer) =>
        {
            if (message == 0x1010)
            {
                points++;
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                return 0;
            }

            return null;
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(32 | 512 | 1024)];
        if (stage == "action")
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM", args, default));
        }
        else if (stage == "cancellation")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, default));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(stage == "neighbor" ? 2 : 3, points);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }
}
