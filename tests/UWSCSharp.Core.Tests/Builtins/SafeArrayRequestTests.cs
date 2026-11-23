using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SafeArrayRequestTests
{
    private static Value N(double number) => Value.Numeric(number);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    public void ComBooleanBoundsUseTheirVariantIntegerValue(bool flag, int expected)
    {
        Value value = N(flag ? 1 : 0) with
        {
            NativeVariantType = 11,
            NativeScalar = flag
        };
        var request = SafeArrayRequest.Read([value, value]);
        Assert.Equal([expected], request.LowerBounds);
        Assert.Equal([expected], request.UpperBounds);
    }

    [Fact]
    public void ExplicitEmptyIsNotAnOmittedNumericArgument() =>
        Assert.Throws<InvalidOperationException>(() =>
            SafeArrayRequest.Read([Value.Empty, Value.Empty]));

    [Fact]
    public void DefaultAndSingleArgumentKeepObservedEmptyBounds()
    {
        foreach (Value[] values in new[] { Array.Empty<Value>(), [N(1)] })
        {
            var request = SafeArrayRequest.Read(values);
            Assert.Equal([1], request.LowerBounds);
            Assert.Equal([0], request.UpperBounds);
            Assert.Equal(0, request.ElementCount);
            Assert.Equal(12, request.ElementType);
        }
        var omittedUpper = SafeArrayRequest.Read([N(0), new(ValueKind.Omitted)]);
        Assert.Equal([0], omittedUpper.UpperBounds);
        Assert.Equal(1, omittedUpper.ElementCount);
    }

    [Theory]
    [InlineData(1.8, 2.8, 1, 2)]
    [InlineData(-2.8, -1.8, -2, -1)]
    [InlineData(0.5, 2.5, 0, 2)]
    [InlineData(-2.5, -0.5, -2, 0)]
    [InlineData(int.MinValue, int.MinValue, int.MinValue, int.MinValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void BoundsTruncateWithoutOverflow(double lower, double upper,
        int expectedLower, int expectedUpper)
    {
        var request = SafeArrayRequest.Read([N(lower), N(upper)]);
        Assert.Equal([expectedLower], request.LowerBounds);
        Assert.Equal([expectedUpper], request.UpperBounds);
        Assert.Equal((long)expectedUpper - expectedLower + 1, request.ElementCount);
    }

    [Fact]
    public void AsymmetricAndEmptyAxesRetainTheirOrder()
    {
        var request = SafeArrayRequest.Read([N(-2), N(-1), N(5), N(7)]);
        Assert.Equal([-2, 5], request.LowerBounds);
        Assert.Equal([-1, 7], request.UpperBounds);
        Assert.Equal(6, request.ElementCount);
        Assert.Equal(0, SafeArrayRequest.Read([N(3), N(2), N(5), N(7)]).ElementCount);
        Assert.Equal(0, SafeArrayRequest.Read([N(-2), N(-1), N(5), N(4)]).ElementCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void TypeOccupiesThirdOrFifthPosition(int type)
    {
        Assert.Equal(type, SafeArrayRequest.Read([N(0), N(1), N(type)]).ElementType);
        Assert.Equal(type, SafeArrayRequest.Read([N(0), N(1), N(0), N(2), N(type)]).ElementType);
    }

    [Fact]
    public void OptionalTypeAndNumericTextUseObservedConversions()
    {
        Assert.Equal(12, SafeArrayRequest.Read([N(0), N(1), new(ValueKind.Omitted)]).ElementType);
        Assert.Equal(12, SafeArrayRequest.Read([N(0), N(1), N(0), N(2), new(ValueKind.Omitted)]).ElementType);
        Assert.Equal(3, SafeArrayRequest.Read([Value.String("3"), Value.String("4"), N(3.8)]).ElementType);
        Assert.Equal(3, SafeArrayRequest.Read([N(0), N(1), Value.String("3")]).ElementType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(8204)]
    public void UnsupportedTypesAreRejected(int type) =>
        Assert.Throws<InvalidOperationException>(() => SafeArrayRequest.Read([N(0), N(0), N(type)]));

    [Theory]
    [InlineData(999999)]
    [InlineData(1000000)]
    public void ElementLimitIsCheckedWithoutAllocatingValues(int count)
    {
        var request = SafeArrayRequest.Read([N(0), N(count - 1)]);
        Assert.Equal(count, request.ElementCount);
    }

    [Fact]
    public void InvalidAndExcessiveShapesAreRejectedBeforeAllocation()
    {
        foreach (Value[] values in new[]
        {
            new[] { N(3), N(1) },
            [N(0), N(1000000)],
            [N(0), N(999999), N(0), N(999999)],
            [N(int.MinValue), N(int.MaxValue)],
            [N(2147483648d), N(2147483648d)],
            [N(-2147483649d), N(-2147483649d)],
            [Value.String("bad"), N(0)],
            [new Value(ValueKind.Number, double.NaN), N(0)],
            [new Value(ValueKind.Number, double.PositiveInfinity), N(0)]
        })
        {
            Assert.Throws<InvalidOperationException>(() => SafeArrayRequest.Read(values));
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 2)]
    [InlineData(5, 3)]
    public void RequiredPositionsRejectOmission(int count, int omitted)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SafeArrayRequest.ValidatePositions(count, index => index == omitted));
    }

    [Fact]
    public void ExcessArgumentsAreRejectedBeforeInspectingTheirPositions()
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() =>
            SafeArrayRequest.ValidatePositions(6, _ => { calls++; return false; }));
        Assert.Equal(0, calls);
    }
}
