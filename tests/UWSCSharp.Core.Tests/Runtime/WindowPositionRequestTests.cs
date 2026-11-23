using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowPositionRequestTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-80.9, 240.75, -80, 240)]
    [InlineData(2147483647.75, -2147483648.75,
        int.MaxValue, int.MinValue)]
    public void CoordinatesTruncateTowardZeroBeforeRangeValidation(
        double x, double y, int expectedX, int expectedY)
    {
        Assert.Equal(new ScriptWindowPosition(expectedX, expectedY),
            ScriptWindowPosition.Read([Value.Numeric(x), Value.Numeric(y)]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(2147483648)]
    [InlineData(-2147483649)]
    public void EitherInvalidAxisRejectsTheEntireRequest(double invalid)
    {
        var value = new Value(ValueKind.Number, invalid);
        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowPosition.Read([value, Value.Numeric(1)]));
        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowPosition.Read([Value.Numeric(1), value]));
    }

    [Fact]
    public void BothCoordinatesAreRequiredAndMustBeNumeric()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowPosition.Read([]));
        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowPosition.Read([Value.Numeric(1)]));
        Assert.Throws<InvalidOperationException>(() =>
            ScriptWindowPosition.Read([
                Value.Numeric(1), Value.Numeric(2), Value.Numeric(3)]));
        foreach (Value invalid in new Value[]
            { new(ValueKind.Omitted), Value.String("invalid") })
        {
            Assert.Throws<InvalidOperationException>(() =>
                ScriptWindowPosition.Read([invalid, Value.Numeric(1)]));
            Assert.Throws<InvalidOperationException>(() =>
                ScriptWindowPosition.Read([Value.Numeric(1), invalid]));
        }
    }
}
