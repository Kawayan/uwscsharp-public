using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowPositionGeometryTests
{
    [Theory]
    [InlineData(200, 500, 200, 280)]
    [InlineData(200, 220, 200, 0)]
    [InlineData(200, 100, 200, 240)]
    [InlineData(900, 100, 600, 240)]
    [InlineData(-80, -90, 0, 50)]
    [InlineData(int.MaxValue, int.MaxValue, 600, 580)]
    [InlineData(int.MinValue, int.MinValue, 0, 0)]
    public void LogUsesActualSizesAndWorkArea(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal((expectedX, expectedY), WindowsScriptWindowLayout.LogPosition(
            (x, y, 320, 140), 400, 220,
            new Native.Rect { Left = 0, Top = 0, Right = 1000, Bottom = 800 }));
    }

    [Fact]
    public void NegativeMonitorAndOversizedLogKeepAnAccessibleTopLeft()
    {
        var work = new Native.Rect { Left = -1900, Top = -700, Right = -900, Bottom = 100 };
        Assert.Equal((-1800, -650), WindowsScriptWindowLayout.LogPosition(
            (-1800, -430, 320, 140), 400, 220, work));
        Assert.Equal((-1900, -700), WindowsScriptWindowLayout.LogPosition(
            (-1800, -430, 320, 140), int.MaxValue, int.MaxValue, work));
    }
}
