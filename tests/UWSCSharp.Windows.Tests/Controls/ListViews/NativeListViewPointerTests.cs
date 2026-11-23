using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewPointerTests
{
    private sealed class Platform(List<string> events,
        CancellationTokenSource cancellation, string? outcome) : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal List<uint> Posts { get; } = [];

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            new(root, Buffers);

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.Equal(77, root);
            Assert.True(Buffers.Released);
            Assert.True(Buffers.Closed);
            events.Add("restore");
        }

        public override Native.Point ToScreen(nint target, Native.Point point)
        {
            Assert.Equal(10, point.X);
            Assert.Equal(20, point.Y);
            events.Add("screen");
            if (outcome == "screen")
            {
                cancellation.Cancel();
            }

            return outcome == "overflow" ? new(int.MaxValue, 20) : new(1010, -480);
        }

        public override void MoveCursor(Native.Point point)
        {
            Assert.Equal(1015, point.X);
            Assert.Equal(-475, point.Y);
            events.Add("move");
            if (outcome == "move")
            {
                cancellation.Cancel();
            }
        }

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            Assert.Equal(0, parameter);
            Assert.Equal((nint)0x0018000e, data);
            Posts.Add(message);
            events.Add($"post:{message:x}");
            if (message == 0x201)
            {
                if (outcome == "post")
                {
                    cancellation.Cancel();
                }

                if (outcome == "throw_post")
                {
                    throw new InvalidOperationException("post callback");
                }
            }
        }
    }

    public static TheoryData<int, bool, bool, uint[]> Cases => new()
    {
        { 0, false, false, [] },
        { 0, true, false, [] },
        { 1024, true, false, [] },
        { 1024, false, false, [] },
        { 1024, true, true, [] },
        { 1024, false, true, [] },
        { 2048, true, true, [0x201, 0x202] },
        { 2048, false, true, [0x201, 0x202] },
        { 4096, true, false, [0x204, 0x205, 0x205] },
        { 8192, true, false, [0x201, 0x202, 0x203, 0x202] },
        { 4096 | 8192, true, false, [0x204, 0x205, 0x205, 0x206, 0x205, 0x205] },
        { 2048 | 4096, true, false, [0x204, 0x205, 0x205] },
        { 1024 | 2048, true, true, [0x201, 0x202] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FlagsPostAtPointPlusFourAndMoveAfterAction(int flags, bool on,
        bool checkbox, uint[] expected)
    {
        await Run(flags, on, checkbox, expected, null);
    }

    [Theory]
    [InlineData("point")]
    [InlineData("key")]
    [InlineData("post")]
    [InlineData("screen")]
    [InlineData("move")]
    [InlineData("throw_post")]
    [InlineData("overflow")]
    public async Task CancellationAndFailureKeepCompletedEffectsAndRelease(string outcome)
    {
        uint[] expected = outcome is "point" or "key" ? [] : [0x201, 0x202];
        await Run(outcome == "key" ? 1024 : 1024 | 2048, true, true, expected, outcome);
    }

    private static async Task Run(int flags, bool on, bool checkbox,
        uint[] expected, string? outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "item");
        if (checkbox)
        {
            Native.SendMessageW(view, 0x1036, 4, 4);
        }

        using var cancellation = new CancellationTokenSource();
        List<string> events = [];
        var platform = new Platform(events, cancellation, outcome);
        List<nint> pointers = [];
        int points = 0;
        int keys = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, parameter, data) =>
        {
            if (message == 0x1010)
            {
                points++;
                events.Add("point");
                pointers.Add(data);
                Marshal.WriteInt32(data, points == 3 ? 10 : 50);
                Marshal.WriteInt32(data, 4, points == 3 ? 20 : 60);
                if (points == 3 && outcome == "point")
                {
                    cancellation.Cancel();
                }

                return 0;
            }

            if (message == 0x100 && parameter is 13 or 32)
            {
                keys++;
                events.Add($"key:{parameter}");
                if (outcome == "key")
                {
                    cancellation.Cancel();
                }
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("item"), Value.Numeric(32 | 512 | flags), Value.Numeric(on ? 1 : 0)];
        if (outcome is null)
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }
        else if (outcome is "throw_post" or "overflow")
        {
            Assert.Equal(Value.Numeric(0),
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }

        Assert.Equal(3, points);
        Assert.All(pointers, pointer => Assert.Equal(platform.Buffers.Address, pointer));
        Assert.Equal(expected, platform.Posts);
        Assert.Equal((outcome is null && (flags & 0x3800) == 0 && on) || outcome == "key" ? 1 : 0, keys);
        bool moved = (flags & 1024) != 0 && (outcome is null or "move");
        Assert.Equal(moved, events.Contains("move"));
        if (moved)
        {
            int action = events.FindLastIndex(value => value.StartsWith("key:", StringComparison.Ordinal)
                || value.StartsWith("post:", StringComparison.Ordinal));
            Assert.True(events.IndexOf("screen") > action);
            Assert.Equal("move", events[^2]);
        }

        Assert.Equal("restore", events[^1]);
        Assert.Equal((nint)3, Native.SendMessageW(view, 0x102c, 0, 3));
        if (checkbox)
        {
            bool checkedItem = ((flags & 0x3800) == 0 && on) && (outcome is null or "key");
            Assert.Equal((nint)(checkedItem ? 0x2000 : 0x1000),
                Native.SendMessageW(view, 0x102c, 0, 0xf000));
        }

        Assert.Null(fixture.CallbackFailure);
    }
}
