using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ProviderItemMouseTests
{
    private sealed class Platform : NativeListSelectionPlatform
    {
        internal List<string> Events { get; } = [];
        internal bool PointerReady
        {
            get; private set;
        }
        internal bool OwnsTarget { get; set; } = true;
        internal Action? PointerChecked
        {
            get; set;
        }

        public override bool OwnsPointer(nint root, Native.Point point)
        {
            PointerChecked?.Invoke();
            return OwnsTarget;
        }

        public override int Metric(int index) => index switch
        {
            76 => -100,
            77 => -50,
            78 => 1000,
            79 => 600,
            _ => throw new InvalidOperationException()
        };

        public override void MoveCursor(Native.Point point)
        {
            PointerReady = false;
            Events.Add($"move:{point.X},{point.Y}");
        }

        public override void Foreground(nint window) =>
            Events.Add("foreground");

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PointerReady = true;
            Events.Add($"sleep:{milliseconds}");
        }
    }

    [Fact]
    public void DownWaitsForTheLastPointerMove()
    {
        var platform = new Platform();
        var mouse = new ProviderItemMouse(_ => Assert.True(platform.PointerReady),
            false, platform);
        mouse.Click(0, new Native.Point(30, 40), ClickItemFlags.LeftClick, default);
    }

    [Fact]
    public void ForeignPointerTargetIsRejectedBeforeDown()
    {
        var platform = new Platform { OwnsTarget = false };
        var mouse = new ProviderItemMouse(_ => throw new InvalidOperationException(),
            false, platform);
        Assert.False(mouse.Click(0, new Native.Point(30, 40),
            ClickItemFlags.LeftClick, default));
    }

    [Fact]
    public void CancellationDuringPointerCheckDoesNotSendDown()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { PointerChecked = cancellation.Cancel };
        List<uint> inputs = [];
        var mouse = new ProviderItemMouse(input => inputs.Add(input.Data.Mouse.Flags),
            false, platform);
        Assert.Throws<OperationCanceledException>(() => mouse.Click(0,
            new Native.Point(30, 40), ClickItemFlags.LeftClick, cancellation.Token));
        Assert.Empty(inputs);
    }

    [Theory]
    [InlineData(10, 20, 100, 40, 1024, 60, 40)]
    [InlineData(10, 20, 100, 40, 2048, 18, 28)]
    [InlineData(10, 20, 16, 16, 4096, 14, 24)]
    [InlineData(10, 20, 8, 8, 8192, 14, 24)]
    [InlineData(11, 21, 1, 1, 2048, 11, 21)]
    [InlineData(11, 21, 3, 3, 1024, 12, 22)]
    [InlineData(-110, -60, 100, 100, 2048, -100, -50)]
    [InlineData(890, 540, 100, 100, 1024, 899, 549)]
    public void PointUsesCenterOrInsetAndClipsToVirtualScreen(
        int x, int y, int width, int height, int flags, int expectedX, int expectedY)
    {
        var platform = new Platform();
        var mouse = new ProviderItemMouse(_ => throw new InvalidOperationException(),
            false, platform);
        Native.Point? point = mouse.Move(x, y, width, height,
            (ClickItemFlags)flags, default);
        Assert.NotNull(point);
        Assert.Equal(expectedX, point.Value.X);
        Assert.Equal(expectedY, point.Value.Y);
        Assert.Equal(new[] { $"move:{expectedX},{expectedY}" }, platform.Events);
    }

    [Theory]
    [InlineData(900, 0, 10, 10)]
    [InlineData(0, 550, 10, 10)]
    [InlineData(-200, 0, 100, 10)]
    [InlineData(0, -100, 10, 50)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -1)]
    public void UnavailableRectangleDoesNotMove(int x, int y, int width, int height)
    {
        var platform = new Platform();
        var mouse = new ProviderItemMouse(_ => throw new InvalidOperationException(),
            false, platform);
        Assert.Null(mouse.Move(x, y, width, height, ClickItemFlags.LeftClick, default));
        Assert.Empty(platform.Events);
    }

    [Theory]
    [InlineData(2048, false, new uint[] { 2, 4 })]
    [InlineData(4096, false, new uint[] { 8, 16 })]
    [InlineData(8192, false, new uint[] { 2, 4, 2, 4 })]
    [InlineData(10240, false, new uint[] { 2, 4 })]
    [InlineData(12288, false, new uint[] { 8, 16 })]
    [InlineData(2048, true, new uint[] { 8, 16 })]
    [InlineData(4096, true, new uint[] { 2, 4 })]
    public void ClickUsesExplicitPrecedenceAndSwappedButtons(
        int flags, bool swapped, uint[] expected)
    {
        var platform = new Platform();
        List<uint> inputs = [];
        var mouse = new ProviderItemMouse(input => inputs.Add(input.Data.Mouse.Flags),
            swapped, platform);
        mouse.Click(0, new(12, 34), (ClickItemFlags)flags, default);
        Assert.Equal(expected, inputs);
        Assert.Equal(expected.Length == 4 ? 1 : 0,
            platform.Events.Count(value => value == "sleep:100"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAndSendFailureStillRelease(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        List<uint> inputs = [];
        var mouse = new ProviderItemMouse(input =>
        {
            uint flags = input.Data.Mouse.Flags;
            inputs.Add(flags);
            if (flags == 2)
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new InvalidOperationException("down失敗");
                }
            }
        }, false, new Platform());
        if (cancel)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => mouse.Click(0,
                new(12, 34), ClickItemFlags.DoubleClick, cancellation.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => mouse.Click(0,
                new(12, 34), ClickItemFlags.DoubleClick, cancellation.Token));
        }

        Assert.Equal(new uint[] { 2, 4 }, inputs);
    }

    [Fact]
    public void ReleaseFailurePreservesPrimaryAndDoesNotRepeatDown()
    {
        var down = new InvalidOperationException("down失敗");
        var up = new InvalidOperationException("up失敗");
        List<uint> inputs = [];
        var mouse = new ProviderItemMouse(input =>
        {
            inputs.Add(input.Data.Mouse.Flags);
            throw input.Data.Mouse.Flags == 2 ? down : up;
        }, false, new Platform());
        Assert.Same(down, Assert.Throws<InvalidOperationException>(() =>
            mouse.Click(0, new(12, 34), ClickItemFlags.DoubleClick, default)));
        Assert.Same(up, down.Data[ProviderItemMouse.ReleaseErrorsKey]);
        Assert.Equal(new uint[] { 2, 4 }, inputs);
    }

    [Fact]
    public void InvalidRectangleAndPrecancelDoNotMoveOrClick()
    {
        var platform = new Platform();
        var mouse = new ProviderItemMouse(_ => throw new InvalidOperationException(),
            false, platform);
        Assert.Throws<InvalidOperationException>(() => mouse.Move(double.NaN,
            0, 10, 10, ClickItemFlags.MouseMove, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => mouse.Click(0,
            new(12, 34), ClickItemFlags.LeftClick, cancellation.Token));
        Assert.Empty(platform.Events);
    }
}
