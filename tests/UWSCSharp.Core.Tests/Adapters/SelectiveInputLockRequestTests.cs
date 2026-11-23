using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputLockRequestTests
{
    [Fact]
    public void ReleaseAndExplicitZeroRemainDistinct()
    {
        var release = SelectiveInputLockRequest.Read([]);
        Assert.True(release.IsRelease);
        Assert.Null(release.WindowId);
        var all = SelectiveInputLockRequest.Read([Value.Numeric(0)]);
        Assert.False(all.IsRelease);
        Assert.Equal(0, all.WindowId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void AcceptsIntegerIdBoundariesAndNumericText(int id)
    {
        foreach (Value value in new[] { Value.Numeric(id),
            Value.String(id.ToString(CultureInfo.InvariantCulture)) })
        {
            var request = SelectiveInputLockRequest.Read([value]);
            Assert.Equal(id, request.WindowId);
            Assert.Equal(0, request.Mode);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AcceptsOnlyPublicModes(int mode)
    {
        foreach (Value value in new[] { Value.Numeric(mode),
            Value.String(mode.ToString(CultureInfo.InvariantCulture)) })
        {
            Assert.Equal(mode, SelectiveInputLockRequest.Read(
                [Value.Numeric(1), value]).Mode);
        }
    }

    [Fact]
    public void OmittedModeDefaultsToZero()
    {
        Assert.Equal(0, SelectiveInputLockRequest.Read(
            [Value.Numeric(1), new Value(ValueKind.Omitted)]).Mode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(2147483648d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidIdNumbersAndText(double number)
    {
        foreach (Value value in new[] { new Value(ValueKind.Number, number),
            Value.String(number.ToString(CultureInfo.InvariantCulture)) })
        {
            AssertError([value]);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidModeNumbersAndText(double number)
    {
        foreach (Value value in new[] { new Value(ValueKind.Number, number),
            Value.String(number.ToString(CultureInfo.InvariantCulture)) })
        {
            AssertError([Value.Numeric(1), value]);
        }
    }

    [Theory]
    [InlineData(ValueKind.Empty)]
    [InlineData(ValueKind.Null)]
    [InlineData(ValueKind.Array)]
    [InlineData(ValueKind.Omitted)]
    [InlineData(ValueKind.Nothing)]
    [InlineData(ValueKind.Error)]
    [InlineData(ValueKind.Object)]
    [InlineData(ValueKind.HashTable)]
    public void RejectsNonNumericKindsWithoutInterpretingThemAsZero(ValueKind kind)
    {
        var value = new Value(kind);
        AssertError([value]);
        if (kind != ValueKind.Omitted)
        {
            AssertError([Value.Numeric(1), value]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("1\0")]
    [InlineData("$1")]
    [InlineData("1,000")]
    public void RejectsTextOutsideRuntimeNumberContract(string text)
    {
        AssertError([Value.String(text)]);
        AssertError([Value.Numeric(1), Value.String(text)]);
    }

    [Theory]
    [InlineData(" 1 ", 1)]
    [InlineData("1e1", 10)]
    [InlineData("-0", 0)]
    public void UsesExistingRuntimeNumericTextRules(string text, int expected)
    {
        Assert.Equal(expected,
            SelectiveInputLockRequest.Read([Value.String(text)]).WindowId);
    }

    [Fact]
    public void ComBooleanUsesExistingSignedConversion()
    {
        var comTrue = new Value(ValueKind.Number, 1)
        {
            NativeVariantType = 11,
            NativeScalar = true
        };
        var comFalse = new Value(ValueKind.Number, 0)
        {
            NativeVariantType = 11,
            NativeScalar = false
        };
        AssertError([comTrue]);
        AssertError([Value.Numeric(1), comTrue]);
        Assert.Equal(0, SelectiveInputLockRequest.Read([comFalse]).WindowId);
        Assert.Equal(0, SelectiveInputLockRequest.Read([Value.Numeric(1), comFalse]).Mode);
        Assert.Equal(1, SelectiveInputLockRequest.Read([Value.Numeric(1)]).WindowId);
    }

    [Fact]
    public void RejectsExtraArguments()
    {
        AssertError([Value.Numeric(0), Value.Numeric(0), Value.Numeric(0)]);
    }

    private static void AssertError(IReadOnlyList<Value> arguments)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SelectiveInputLockRequest.Read(arguments));
        Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
    }
}
