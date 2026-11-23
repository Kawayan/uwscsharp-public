using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の画面一覧と座標だけを使い、実画面・入力・DPIは操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DisplayEnvironmentTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1920)]
    [InlineData(3, 1080)]
    public void PrimaryIsZeroEvenWhenEnumeratedAfterSecondary(
        int item, int expected)
    {
        var display = new DisplayEnvironment(new Platform());
        Assert.Equal(expected,
            display.Query(new MonitorRequest(0, (MonitorItem)item), default).Number);
        Assert.Equal("primary", display.Query(
            new MonitorRequest(0, MonitorItem.Name), default).Text);
    }

    [Fact]
    public void NegativeCoordinatesAndAllMonitorItemsArePreserved()
    {
        var display = new DisplayEnvironment(new Platform());
        Assert.Equal(2, display.Query(null, default).Number);
        Assert.Equal(-1280, display.Query(new(1, MonitorItem.X), default).Number);
        Assert.Equal(-50, display.Query(new(1, MonitorItem.Y), default).Number);
        Assert.Equal(1280, display.Query(new(1, MonitorItem.Width), default).Number);
        Assert.Equal(1024, display.Query(new(1, MonitorItem.Height), default).Number);
        Assert.Equal("left", display.Query(new(1, MonitorItem.Name), default).Text);
        Assert.Equal(1, display.Query(new(0, MonitorItem.Primary), default).Number);
        Assert.Equal(0, display.Query(new(1, MonitorItem.Primary), default).Number);
        Assert.Equal(0, display.Query(new(-1, MonitorItem.X), default).Number);
        Assert.Equal(0, display.Query(new(999, MonitorItem.Name), default).Number);
    }

    [Fact]
    public void EveryReadRefreshesCursorScreenAndTopology()
    {
        var platform = new Platform();
        var display = new DisplayEnvironment(platform);
        Assert.Equal(-25, display.Read(WindowsDisplayVariable.MouseX, default).Number);
        Assert.Equal(33, display.Read(WindowsDisplayVariable.MouseY, default).Number);
        platform.Cursor = new Native.Point { X = 444, Y = -111 };
        Assert.Equal(444, display.Read(WindowsDisplayVariable.MouseX, default).Number);
        Assert.Equal(-111, display.Read(WindowsDisplayVariable.MouseY, default).Number);
        Assert.Equal(1920, display.Read(WindowsDisplayVariable.ScreenWidth, default).Number);
        Assert.Equal(1080, display.Read(WindowsDisplayVariable.ScreenHeight, default).Number);
        Assert.Equal(32, display.Read(WindowsDisplayVariable.ScreenBitsPerPixel, default).Number);
        platform.Monitors = [new(8, 0, 0, 800, 600, "new primary", true)];
        platform.Bits = 24;
        Assert.Equal(1, display.Query(null, default).Number);
        Assert.Equal(800, display.Read(WindowsDisplayVariable.ScreenWidth, default).Number);
        Assert.Equal(600, display.Read(WindowsDisplayVariable.ScreenHeight, default).Number);
        Assert.Equal(24, display.Read(WindowsDisplayVariable.ScreenBitsPerPixel, default).Number);
    }

    [Fact]
    public void EmptyTopologyReturnsZeroButPrimaryInformationFails()
    {
        var display = new DisplayEnvironment(new Platform { Monitors = [] });
        Assert.Equal(0, display.Query(null, default).Number);
        Assert.Equal(0, display.Query(new(0, MonitorItem.Name), default).Number);
        Assert.Throws<InvalidOperationException>(() =>
            display.Read(WindowsDisplayVariable.ScreenWidth, default));
    }

    [Fact]
    public void BadPrimaryShapeAndBitsAreRejected()
    {
        var platform = new Platform
        {
            Monitors = [new(1, 0, 0, 100, 100, "no primary", false)]
        };
        var display = new DisplayEnvironment(platform);
        Assert.Throws<InvalidOperationException>(() => display.Query(null, default));
        platform.Monitors = [new(1, 0, 0, 0, 100, "bad width", true)];
        Assert.Throws<InvalidOperationException>(() => display.Query(null, default));
        platform.Bits = 0;
        Assert.Throws<InvalidOperationException>(() =>
            display.Read(WindowsDisplayVariable.ScreenBitsPerPixel, default));
    }

    [Fact]
    public void CancellationBeforeAndAfterEnumerationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnEnumerate = cancellation.Cancel };
        var display = new DisplayEnvironment(platform);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            display.Query(null, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            display.Read(WindowsDisplayVariable.MouseX, cancellation.Token));
        Assert.Equal(0, platform.CursorReads);
    }

    private sealed class Platform : IDisplayPlatform
    {
        internal IReadOnlyList<DisplayMonitor> Monitors =
        [
            new(2, -1280, -50, 1280, 1024, "left", false),
            new(1, 0, 0, 1920, 1080, "primary", true)
        ];
        internal Native.Point Cursor = new() { X = -25, Y = 33 };
        internal int Bits = 32;
        internal int CursorReads;
        internal Action? OnEnumerate;

        public IReadOnlyList<DisplayMonitor> Enumerate(CancellationToken token)
        {
            OnEnumerate?.Invoke();
            return Monitors;
        }

        public Native.Point CursorPosition()
        {
            CursorReads++;
            return Cursor;
        }

        public int BitsPerPixel() => Bits;
    }
}
