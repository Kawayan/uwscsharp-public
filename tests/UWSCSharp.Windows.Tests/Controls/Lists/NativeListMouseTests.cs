using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeListMouseTests
{
    [Theory]
    [InlineData(0, 1024)]
    [InlineData(0, 2048)]
    [InlineData(0, 4096)]
    [InlineData(0, 8192)]
    [InlineData(0, 3072)]
    [InlineData(0, 10240)]
    [InlineData(0x800, 1024)]
    [InlineData(0x800, 2048)]
    [InlineData(0x800, 4096)]
    [InlineData(0x800, 8192)]
    [InlineData(0x800, 3072)]
    [InlineData(0x800, 10240)]
    public async Task SessionScrollsToTheMatchedRowAndRoutesItsMouseFlags(int style, int flags)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", style, 10, height: 70);
        for (int index = 0; index < 40; index++)
        {
            Native.SendText(list, 0x180, 0, $"row{index}", 2, 1000, out _);
        }
        var platform = new Platform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("row30"), Value.Numeric(2 | 512 | flags)], default));
        int top = (int)Native.SendMessageW(list, 0x18e, 0, 0);
        int height = (int)Native.SendMessageW(list, 0x1a1, 0, 0);
        int y = (30 - top) * height + height / 2;
        Assert.InRange(y, 0, 69);
        if ((flags & 1024) != 0)
        {
            Assert.Equal(new Native.Point(4, platform.ExpectedY), Assert.Single(platform.Moves));
        }
        else
        {
            Assert.Empty(platform.Moves);
        }
        if ((flags & 0x3800) != 0)
        {
            Assert.NotEmpty(platform.Posts);
            Assert.All(platform.Posts, post =>
            {
                Assert.Equal(list, post.Target);
                Assert.Equal((nint)((y << 16) | 4), post.Data);
            });
            uint expected = (flags & 4096) != 0 ? 0x204u : 0x201u;
            Assert.Equal(expected, platform.Posts[0].Message);
        }
        else
        {
            Assert.Empty(platform.Posts);
        }
        if (style != 0)
        {
            Assert.Equal((nint)1, Native.SendMessageW(list, 0x187, 30, 0));
        }
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void CancellationDuringDownStillRequestsRelease()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 0, 10);
        Native.SendText(list, 0x180, 0, "row", 2, 1000, out _);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { Down = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => NativeListMouse.Apply(
            list, 0, ClickItemFlags.LeftClick, platform, cancellation.Token));
        Assert.Equal(new uint[] { 0x201, 0x202 },
            platform.Posts.Select(post => post.Message).ToArray());
    }

    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal List<Native.Point> Moves { get; } = [];
        internal List<(nint Target, uint Message, nint Data)> Posts { get; } = [];
        internal Action? Down
        {
            get; init;
        }
        internal int ExpectedY
        {
            get; private set;
        }

        public override Native.Point ToScreen(nint window, Native.Point point)
        {
            int top = (int)Native.SendMessageW(window, 0x18e, 0, 0);
            int height = (int)Native.SendMessageW(window, 0x1a1, 0, 0);
            ExpectedY = (30 - top) * height + height / 2;
            return point;
        }

        public override void MoveCursor(Native.Point point) => Moves.Add(point);

        public override void Post(nint window, uint message, nint parameter, nint data)
        {
            Posts.Add((window, message, data));
            if (message == 0x201)
            {
                Down?.Invoke();
            }
        }
    }
}
