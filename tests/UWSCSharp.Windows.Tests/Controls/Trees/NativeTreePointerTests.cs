using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreePointerTests
{
    private sealed class Platform(List<string> events,
        CancellationTokenSource cancellation, string? cancelAt = null) : OwnedListSelectionPlatform
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
            Assert.Equal(14, point.X);
            Assert.Equal(24, point.Y);
            events.Add("screen");
            if (cancelAt == "screen")
            {
                cancellation.Cancel();
            }

            return new(1014, -476);
        }

        public override void MoveCursor(Native.Point point)
        {
            Assert.Equal(1014, point.X);
            Assert.Equal(-476, point.Y);
            events.Add("move");
            if (cancelAt == "move")
            {
                cancellation.Cancel();
            }

            if (cancelAt == "throw_move")
            {
                throw new InvalidOperationException("move callback");
            }
        }

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            Assert.Equal(0, parameter);
            Assert.Equal((nint)0x0018000e, data);
            Posts.Add(message);
            events.Add($"post:{message:x}");
            if (cancelAt == "post" && message == 0x201)
            {
                cancellation.Cancel();
            }

            if (cancelAt == "throw_post" && message == 0x201)
            {
                throw new InvalidOperationException("post callback");
            }
        }
    }

    public static TheoryData<int, uint[]> Flags => new()
    {
        { 0, [] },
        { 1024, [] },
        { 2048, [0x201, 0x202] },
        { 4096, [0x204, 0x205, 0x205] },
        { 8192, [0x201, 0x202, 0x203, 0x202] },
        { 2048 | 8192, [0x201, 0x202, 0x203, 0x202] },
        { 4096 | 8192, [0x204, 0x205, 0x205, 0x206, 0x205, 0x205] },
        { 2048 | 4096, [0x204, 0x205, 0x205] },
        { 1024 | 2048, [0x201, 0x202] },
    };

    [Theory]
    [MemberData(nameof(Flags))]
    public async Task FlagsUseSharedRectangleAndOriginalPostedSequence(int flags, uint[] expected)
    {
        await Run(flags, expected, null);
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("screen")]
    [InlineData("move")]
    [InlineData("post")]
    public async Task CancellationKeepsSelectionAndReleasesBeforeRestore(string stage)
    {
        await Run(2048, stage == "post" ? [0x201, 0x202] : [], stage);
    }

    [Theory]
    [InlineData("throw_move")]
    [InlineData("throw_post")]
    public async Task CallbackExceptionReleasesBeforeRestore(string stage)
    {
        await Run(2048, stage == "throw_post" ? [0x201, 0x202] : [], stage);
    }

    private static async Task Run(int flags, uint[] expected, string? cancelAt)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint item = TreeControlTests.Add(tree, 0, "item");
        using var cancellation = new CancellationTokenSource();
        List<string> events = [];
        var platform = new Platform(events, cancellation, cancelAt);
        nint nameAddress = 0;
        int rectangles = 0;
        int spaces = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, parameter, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                nameAddress = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 16);
            }

            if (message == 0x1114)
            {
                Assert.Equal(item, pointer);
                events.Add("visible");
            }

            if (message == 0x1104)
            {
                rectangles++;
                events.Add("rectangle");
                Assert.Equal(1, parameter);
                Assert.Equal(nameAddress, pointer);
                Assert.Equal(item, Marshal.ReadIntPtr(pointer));
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                Marshal.WriteInt32(pointer, 8, 40);
                Marshal.WriteInt32(pointer, 12, 50);
                if (cancelAt == "rectangle")
                {
                    cancellation.Cancel();
                }

                return 0;
            }

            if (message == 0x100 && parameter == 32)
            {
                spaces++;
            }

            return null;
        });
        Native.ShowWindow(fixture.Root, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("item"), Value.Numeric(16 | flags)];
        if (cancelAt is null)
        {
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        }
        else if (cancelAt.StartsWith("throw", StringComparison.Ordinal))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }

        Assert.Equal(expected, platform.Posts);
        Assert.Equal(flags == 0 ? 0 : 1, rectangles);
        Assert.Equal(cancelAt is null && (flags & 0x3800) == 0 ? 1 : 0, spaces);
        Assert.Equal(item, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Equal("restore", events[^1]);
        if (flags != 0)
        {
            Assert.Equal(["visible", "rectangle"], events.Take(2));
        }

        if (cancelAt is null && flags != 0)
        {
            Assert.Equal(["screen", "move"], events.Skip(2).Take(2));
        }

        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(-1, 24, false)]
    [InlineData(65536, 65535, false)]
    [InlineData(10, -1, true)]
    [InlineData(10, 65536, true)]
    public void PackedCoordinatesTruncateXAndCheckY(int x, int y, bool overflow)
    {
        var platform = new PackedPlatform();
        if (overflow)
        {
            Assert.Throws<OverflowException>(() => NativeControlMouse.Post(
                1, 2048, new(x, y), platform, default));
            Assert.Empty(platform.Data);
        }
        else
        {
            NativeControlMouse.Post(1, 2048, new(x, y), platform, default);
            Assert.Equal(2, platform.Data.Count);
            Assert.All(platform.Data, data =>
                Assert.Equal((uint)(unchecked((ushort)x) | ((uint)y << 16)), unchecked((uint)data)));
        }
    }

    [Fact]
    public void RealGetterReturnsTextRectangleThroughSharedBase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint item = TreeControlTests.Add(tree, 0, "item");
        using var owner = new NativeControlBufferOwner(fixture.Root);
        var buffer = new NativeTreeClickBuffer(owner);
        Assert.Equal("item", buffer.ReadItem(tree, item, default).Name);
        Native.Rect rectangle = buffer.ReadRectangle(tree, item, default);
        nint local = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteIntPtr(local, item);
            Assert.NotEqual(0, Native.SendMessageW(tree, 0x1104, 1, local));
            Native.Rect expected = Marshal.PtrToStructure<Native.Rect>(local);
            Assert.Equal(expected.Left, rectangle.Left);
            Assert.Equal(expected.Top, rectangle.Top);
            Assert.Equal(expected.Right, rectangle.Right);
            Assert.Equal(expected.Bottom, rectangle.Bottom);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    private sealed class PackedPlatform : OwnedListSelectionPlatform
    {
        internal List<nint> Data { get; } = [];

        public override void Post(nint target, uint message, nint parameter, nint data) =>
            Data.Add(data);
    }
}
