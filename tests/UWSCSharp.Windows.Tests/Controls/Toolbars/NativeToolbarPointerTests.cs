using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeToolbarPointerTests
{
    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : ToolbarOperationTests.RecordingPlatform
    {
        internal Native.Point? Moved;
        internal int Waits;

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
                throw new InvalidOperationException("screen callback");
            }

            return new(110, -80);
        }

        public override void MoveCursor(Native.Point point)
        {
            Moved = point;
            Events.Add("move");
            if (failure == "move")
            {
                cancel.Cancel();
            }
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Waits += milliseconds == 20 ? 1 : 0;
        }

        public override void Pump(CancellationToken token) => token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(12288)]
    [InlineData(14336)]
    [InlineData(15360)]
    public async Task ExplicitFlagsBypassSameStateAndOffCleanup(int flags)
    {
        await Run(flags, false, null);
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    public async Task ExplicitFlagsPostEvenWhenOffAlreadyMatches(int flags)
    {
        await Run(flags, false, null, same: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveOnlyUsesScreenCenterBeforeSameStateWait(bool negative)
    {
        await Run(1024, negative, null);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("move")]
    [InlineData("throw")]
    [InlineData("overflow")]
    public async Task InterruptedCursorStepDoesNotPost(string failure)
    {
        await Run(1024 | 2048, false, failure);
    }

    private static async Task Run(int flags, bool negative, string? failure, bool same = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        // 明示postは同値でも動き、OFFでもchecked/pressed解除を行いません。
        ToolbarControlTests.Add(toolbar, "target", 701, state: flags == 1024 || same ? (byte)4 : (byte)7);
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure);
        int cleanup = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x41d)
            {
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                Marshal.WriteInt32(pointer, 8, failure == "overflow" ? int.MinValue : negative ? -1 : 21);
                Marshal.WriteInt32(pointer, 12, negative ? 5 : 35);
                return 0;
            }

            cleanup += message is 0x402 or 0x403 ? 1 : 0;
            return null;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(64 | flags), Value.Numeric(0)];
        if (failure is "overflow" or "throw")
        {
            Assert.Equal(Value.Numeric(1),
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else if (failure is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(0, cleanup);
        if ((flags & 1024) != 0 && failure is not ("screen" or "throw" or "overflow"))
        {
            Assert.NotNull(platform.Moved);
            Assert.Equal(negative ? (105, -87) : (115, -73),
                (platform.Moved.Value.X, platform.Moved.Value.Y));
            string[] cursorEvents = ["screen", "move"];
            Assert.Equal(cursorEvents, platform.Events.Take(2));
        }

        if (failure is not null || flags == 1024)
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
            Assert.All(platform.Posts, post =>
            {
                Assert.Equal(0, post.Parameter);
                Assert.Equal(0x14000a, post.Data);
            });
        }

        Assert.Equal(flags == 1024 ? 1 : 0, platform.Waits);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }
}
