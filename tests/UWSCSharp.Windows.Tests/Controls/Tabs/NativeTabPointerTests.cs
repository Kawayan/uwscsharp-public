using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabPointerTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : ToolbarOperationTests.RecordingPlatform
    {
        internal Native.Point? Moved;

        public override Native.Point ToScreen(nint target, Native.Point point)
        {
            Assert.Equal((10, 20), (point.X, point.Y));
            Events.Add("screen");
            if (failure == "screen")
            {
                cancel.Cancel();
            }

            if (failure == "throw")
            {
                throw new InvalidOperationException("screen失敗");
            }

            return new(110, -80);
        }

        public override void MoveCursor(Native.Point point)
        {
            Events.Add("move");
            Moved = point;
            if (failure == "move")
            {
                cancel.Cancel();
            }
        }

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token) =>
            token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1024, false)]
    [InlineData(1024, true)]
    [InlineData(2048, false)]
    [InlineData(4096, false)]
    [InlineData(8192, false)]
    [InlineData(12288, false)]
    [InlineData(14336, false)]
    [InlineData(15360, false)]
    public async Task SelectionThenRectangleMoveAndExplicitPosts(int flags, bool negative)
    {
        await Run(flags, negative, null);
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("screen")]
    [InlineData("move")]
    [InlineData("throw")]
    [InlineData("overflow")]
    [InlineData("post-cancel")]
    [InlineData("post-throw")]
    [InlineData("mismatch")]
    public async Task StopsAtFailureBoundaryAndReleasesBeforeRestore(string failure)
    {
        await Run(1024 | 2048, false, failure);
    }

    private static async Task Run(int flags, bool negative, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "first", "target");
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure) { Restoring = () => { } };
        List<string> events = platform.Events;
        int rectangles = 0;
        fixture.Notify = (_, pointer) =>
        {
            int code = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code;
            events.Add("notice" + code);
            return 1;
        };
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, index, pointer) =>
        {
            if (message == 0x130b)
            {
                events.Add("selected");
                return failure == "mismatch" ? 0 : null;
            }

            if (message != 0x130a)
            {
                return null;
            }

            rectangles++;
            events.Add("rectangle");
            Assert.Equal(1, index);
            Assert.Equal(platform.Buffers.Address, pointer);
            Assert.Contains("selected", events);
            Assert.Contains("notice-551", events);
            Marshal.WriteInt32(pointer, 10);
            Marshal.WriteInt32(pointer, 4, 20);
            Marshal.WriteInt32(pointer, 8,
                failure == "overflow" ? int.MinValue : negative ? -1 : 21);
            Marshal.WriteInt32(pointer, 12, negative ? 5 : 35);
            if (failure == "rectangle")
            {
                cancel.Cancel();
            }

            return 0;
        });
        platform.Posting = _ =>
        {
            if (failure == "post-cancel")
            {
                cancel.Cancel();
            }

            if (failure == "post-throw")
            {
                throw new InvalidOperationException("post失敗");
            }
        };
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(8 | 512 | flags)];
        if (failure == "overflow")
        {
            await Assert.ThrowsAsync<OverflowException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (failure is "throw" or "post-throw")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (failure is not null and not "mismatch")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(failure == "mismatch" ? 0 : 1),
                await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(flags == 0 || failure == "mismatch" ? 0 : 1, rectangles);
        if ((flags & 1024) != 0 && failure is not ("rectangle" or "screen" or "throw" or "overflow" or "mismatch"))
        {
            Assert.NotNull(platform.Moved);
            Assert.Equal(negative ? (105, -87) : (115, -73),
                (platform.Moved.Value.X, platform.Moved.Value.Y));
            Assert.True(events.IndexOf("rectangle") < events.IndexOf("screen"));
            Assert.True(events.IndexOf("screen") < events.IndexOf("move"));
        }
        else
        {
            Assert.Null(platform.Moved);
        }

        if (failure is "post-cancel" or "post-throw")
        {
            Assert.Equal(new uint[] { 0x201, 0x202 }, platform.Posts.Select(post => post.Message));
        }
        else if (failure is not null || (flags & 0x3800) == 0)
        {
            Assert.Empty(platform.Posts);
        }
        else
        {
            bool right = (flags & 4096) != 0;
            uint up = right ? 0x205u : 0x202u;
            List<uint> expected = [right ? 0x204u : 0x201u, up];
            if (right)
            {
                expected.Add(up);
            }

            if ((flags & 8192) != 0)
            {
                expected.AddRange([right ? 0x206u : 0x203u, up]);
                if (right)
                {
                    expected.Add(up);
                }
            }

            Assert.Equal(expected, platform.Posts.Select(post => post.Message));
        }

        Assert.All(platform.Posts, post =>
        {
            Assert.Equal(tab, post.Target);
            Assert.Equal(0, post.Parameter);
            Assert.Equal(0x14000a, post.Data);
        });
        if (platform.Posts.Count != 0)
        {
            Assert.Equal("priority32", events[^1]);
        }

        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(-1, -2, 0xfffeffffu)]
    [InlineData(65537, 65538, 0x00020001u)]
    [InlineData(int.MinValue, int.MaxValue, 0xffff0000u)]
    public async Task ExplicitPostTruncatesBothCoordinatesToWords(int left, int top, uint packed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        fixture.Notify = (_, _) => 1;
        var platform = new ToolbarOperationTests.RecordingPlatform();
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, _, pointer) =>
        {
            if (message != 0x130a)
            {
                return null;
            }

            Marshal.WriteInt32(pointer, left);
            Marshal.WriteInt32(pointer, 4, top);
            return 0;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(8 | 512 | 2048)], default));
        Assert.Equal(2, platform.Posts.Count);
        Assert.All(platform.Posts, post => Assert.Equal(unchecked((nint)packed), post.Data));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task DefaultNativeQueryDoesNotReadOrSelectTab()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        for (int index = 0; index < 20; index++)
        {
            fixture.Child("Button", "other", 0, 40 + index);
        }

        List<uint> messages = [];
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, _, _) =>
        {
            if (message is 0x1304 or 0x133c or 0x130a or 0x1330 or 0x130c)
            {
                messages.Add(message);
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target")], default));
        Assert.Empty(messages);
        Assert.Null(fixture.CallbackFailure);
    }
}
