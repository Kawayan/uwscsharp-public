using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TextTargetGeometryTests
{
    [Theory]
    [InlineData(100, 200, 130, 230, 104, 204)]
    [InlineData(-100, -50, -90, -40, -96, -46)]
    [InlineData(0, 0, 1, 1, 0, 0)]
    [InlineData(0, 0, 3, 2, 2, 1)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue,
        int.MinValue + 4, int.MinValue + 4)]
    public void MovePointStaysInsideSmallAndNegativeCoordinateTargets(
        int left, int top, int right, int bottom, int x, int y)
    {
        var bounds = new Native.Rect
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
        Assert.Equal(new Native.Point(x, y), TextTargetGeometry.Inside(bounds));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void EmptyAndReversedBoundsHaveNoMovePoint(int width, int height)
    {
        var bounds = new Native.Rect { Right = width, Bottom = height };
        Assert.Throws<InvalidOperationException>(() => TextTargetGeometry.Inside(bounds));
    }

    [Fact]
    public void UiaFractionalBoundsExpandToCoverThePhysicalPixels()
    {
        Native.Rect bounds = TextTargetGeometry.FromUia([-10.5, 20.25, 5.5, 3.5]);
        Assert.Equal(-11, bounds.Left);
        Assert.Equal(20, bounds.Top);
        Assert.Equal(-5, bounds.Right);
        Assert.Equal(24, bounds.Bottom);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, -1, 1)]
    public void InvalidUiaGeometryIsRejected(double x, double y,
        double width, double height)
    {
        Assert.Throws<InvalidOperationException>(() =>
            TextTargetGeometry.FromUia([x, y, width, height]));
    }

    [Fact]
    public void UiaOverflowIsRejectedBeforeNativeInput()
    {
        Assert.Throws<OverflowException>(() =>
            TextTargetGeometry.FromUia([int.MaxValue, 0, 100, 1]));
    }

    [Theory]
    [InlineData("EDIT", "Edit", true)]
    [InlineData("WindowsForms10.EDIT.app.0.123", "Edit", true)]
    [InlineData("RichEdit50W", "Edit", true)]
    [InlineData("WindowsForms10.RichEdit20W.app.0.123", "Edit", true)]
    [InlineData("WindowsForms10.STATIC.app.0.123", "Static", true)]
    [InlineData("CREDIT", "Edit", false)]
    [InlineData("WindowsForms10.Edited.app", "Edit", false)]
    [InlineData("StaticText", "Static", false)]
    public void ClassAliasesDoNotMatchUnrelatedClassNames(
        string name, string standard, bool expected)
    {
        Assert.Equal(expected, NativeTextControlNames.Matches(name, standard));
    }
}
