using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopFormLayoutTests
{
    [Theory]
    [InlineData(80, 90, 80, 90)]
    [InlineData(-80, -90, 0, 0)]
    [InlineData(int.MaxValue, int.MinValue, 680, 0)]
    [InlineData(900, 700, 680, 460)]
    public void PhysicalBoundsKeepTheWholeStopFormInsideTheWorkArea(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal((expectedX, expectedY), WindowsStopFormLayout.Fit(x, y, 320, 140,
            new Native.Rect { Left = 0, Top = 0, Right = 1000, Bottom = 600 }));
    }

    [Fact]
    public void NegativeMonitorCoordinatesArePreserved()
    {
        Assert.Equal((-1500, -400), WindowsStopFormLayout.Fit(-1500, -400, 320, 140,
            new Native.Rect { Left = -1920, Top = -600, Right = 0, Bottom = 0 }));
    }

    [Theory]
    [InlineData(320, 140)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void UnusableWorkAreasAreRejectedWithoutClippingTheButtons(int width, int height)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsStopFormLayout.Fit(0, 0, width, height,
            new Native.Rect { Left = 0, Top = 0, Right = 100, Bottom = 50 }));
    }

    [Theory]
    [InlineData(1, 1, 320, 140)]
    [InlineData(1000, 700, 1000, 700)]
    [InlineData(int.MaxValue, int.MaxValue, 1920, 1040)]
    public void RequestedSizeFitsTheWorkAreaWithoutLosingControls(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), WindowsStopFormLayout.ConstrainSize(
            width, height, 320, 140,
            new Native.Rect { Left = -1920, Top = -200, Right = 0, Bottom = 840 }));
        Assert.Throws<InvalidOperationException>(() => WindowsStopFormLayout.ConstrainSize(
            width, height, 320, 140,
            new Native.Rect { Left = 0, Top = 0, Right = 200, Bottom = 100 }));
    }
}
