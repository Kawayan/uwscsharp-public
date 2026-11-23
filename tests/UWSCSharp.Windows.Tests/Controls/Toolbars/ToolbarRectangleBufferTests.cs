using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ToolbarRectangleBufferTests
{
    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [Fact]
    public void ZeroRectangleResultStillReadsWrittenCoordinatesFromSharedBase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "item", 701);
        using var owner = new NativeControlBufferOwner(fixture.Root);
        nint observed = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message != 0x41d)
            {
                return null;
            }

            observed = pointer;
            Marshal.WriteInt32(pointer, 10);
            Marshal.WriteInt32(pointer, 4, 20);
            Marshal.WriteInt32(pointer, 8, 30);
            Marshal.WriteInt32(pointer, 12, 40);
            return 0;
        });
        var buffer = new NativeToolbarClickBuffer(owner);
        (nint result, Native.Rect rectangle) = buffer.ReadRectangle(toolbar, 0, default);
        Assert.Equal(0, result);
        Assert.Equal(owner.Acquire().Address, observed);
        Assert.Equal((10, 20, 30, 40),
            (rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    public void OrdinaryMatchedButtonsAlsoReadRectangleOnTextAddress(byte style)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "item", 701, style: style);
        nint text = 0;
        List<nint> rectangles = [];
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x44b && pointer != 0)
            {
                text = pointer;
            }

            if (message == 0x41d)
            {
                rectangles.Add(pointer);
            }

            return null;
        });
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            "item", 1, false, default,
            platform: new OwnedListSelectionPlatform()));
        Assert.NotEqual(0, text);
        Assert.NotEmpty(rectangles);
        Assert.Equal(text, rectangles[0]);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("zero")]
    [InlineData("cancel_rect")]
    [InlineData("cancel_post")]
    public async Task RectangleOwnerLivesUntilPostAndRestoration(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "dropdown", 701, style: 128);
        using var cancel = new CancellationTokenSource();
        nint text = 0;
        nint rectangle = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x44b && pointer != 0)
            {
                text = pointer;
            }

            if (message == 0x41d)
            {
                rectangle = pointer;
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                Marshal.WriteInt32(pointer, 8, 30);
                Marshal.WriteInt32(pointer, 12, 40);
                if (outcome == "cancel_rect")
                {
                    cancel.Cancel();
                }

                if (outcome == "zero")
                {
                    return 0;
                }
            }

            return null;
        });
        var platform = new ToolbarOperationTests.RecordingPlatform();
        int restores = 0;
        platform.Restoring = () =>
        {
            restores++;
            Assert.True(platform.Buffers.Released);
            Assert.True(platform.Buffers.Closed);
        };
        platform.Posting = message =>
        {
            Assert.False(platform.Buffers.Released);
            if (message == 0x201 && outcome == "cancel_post")
            {
                cancel.Cancel();
            }
        };
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("dropdown"), Value.Numeric(64)];
        if (outcome.StartsWith("cancel", StringComparison.Ordinal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(text, rectangle);
        Assert.Equal(platform.Buffers.Address, rectangle);
        Assert.Equal(outcome == "cancel_rect" ? 0 : 2, platform.Posts.Count);
        Assert.Equal(1, restores);
        Assert.Null(fixture.CallbackFailure);
    }
}
