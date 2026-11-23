using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopFormRequestTests
{
    [Fact]
    public void OmittedAxesRemainSeparateFromZeroAndNegativeCoordinates()
    {
        var shown = StopFormRequest.Read([Value.Numeric(1)]);
        Assert.True(shown.Visible);
        Assert.Null(shown.X);
        Assert.Null(shown.Y);
        var hidden = StopFormRequest.Read([Value.Numeric(0), Value.Numeric(0), Value.Numeric(-90.9)]);
        Assert.False(hidden.Visible);
        Assert.Equal(0, hidden.X);
        Assert.Equal(-90, hidden.Y);
        var xOnly = StopFormRequest.Read([Value.Numeric(1), Value.Numeric(80)]);
        Assert.Equal(80, xOnly.X);
        Assert.Null(xOnly.Y);
        var yOnly = StopFormRequest.Read([Value.Numeric(1), new(ValueKind.Omitted), Value.Numeric(120)]);
        Assert.Null(yOnly.X);
        Assert.Equal(120, yOnly.Y);
        Assert.Equal(shown, StopFormRequest.Read([Value.Numeric(1), new(ValueKind.Omitted), new(ValueKind.Omitted)]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(2, true)]
    [InlineData(0.5, false)]
    [InlineData(-0.5, false)]
    [InlineData(1.5, true)]
    [InlineData(-1.5, true)]
    public void NumericFlagsUseFiniteNumericConversion(double flag, bool expected)
    {
        Assert.Equal(expected, StopFormRequest.Read([Value.Numeric(flag)]).Visible);
    }

    [Theory]
    [InlineData(null, null, 80, 90)]
    [InlineData(0, null, 0, 90)]
    [InlineData(null, -20, 80, -20)]
    [InlineData(120, 140, 120, 140)]
    [InlineData(-80, -90, -80, -90)]
    public void PositionPreservesOnlyTheOmittedAxes(
        int? x, int? y, int expectedX, int expectedY)
    {
        var request = new StopFormRequest(true, x, y);
        Assert.Equal((expectedX, expectedY), request.ResolvePosition(80, 90));
    }

    public static IEnumerable<object[]> InvalidArguments()
    {
        yield return [Array.Empty<Value>()];
        yield return [new[] { new Value(ValueKind.Omitted) }];
        yield return [new[] { Value.Numeric(1), Value.Numeric(0), Value.Numeric(0), Value.Numeric(0) }];
        yield return [new[] { Value.String("invalid") }];
        yield return [new[] { new Value(ValueKind.Number, double.NaN) }];
        yield return [new[] { new Value(ValueKind.Number, double.PositiveInfinity) }];
        yield return [new[] { Value.Numeric(1), Value.Numeric((double)int.MaxValue + 1) }];
        yield return [new[] { Value.Numeric(0), Value.Numeric(0), new Value(ValueKind.Number, double.NegativeInfinity) }];
        yield return [new[] { Value.Numeric(1), Value.String("invalid") }];
        yield return [new[] { Value.Numeric(1), new Value(ValueKind.Omitted), Value.String("invalid") }];
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidRequestsAreRejectedBeforeNativeWindowChanges(Value[] arguments)
    {
        Assert.Throws<InvalidOperationException>(() => StopFormRequest.Read(arguments));
    }

    [Fact]
    public void FunctionUsesTheHostRatherThanTheWindowsBuiltinRouter()
    {
        Assert.True(BuiltinFunctions.IsBuiltin(BuiltinFunctionNames.StopForm));
        Assert.False(WindowsFunctions.IsBuiltin(BuiltinFunctionNames.StopForm));
    }
}
