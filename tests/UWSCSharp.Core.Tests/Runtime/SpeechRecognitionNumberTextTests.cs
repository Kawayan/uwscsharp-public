using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionNumberTextTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.25, "1.25")]
    [InlineData(-1.25, "-1.25")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1E-5")]
    [InlineData(100000000000000.0, "100000000000000")]
    [InlineData(1000000000000000.0, "1E15")]
    [InlineData(123456789012344.5, "123456789012345")]
    [InlineData(-123456789012344.5, "-123456789012345")]
    [InlineData(123456789012345.5, "123456789012346")]
    [InlineData(999999999999999.5, "1E15")]
    [InlineData(double.MaxValue, "1.79769313486232E308")]
    [InlineData(double.Epsilon, "4.94065645841247E-324")]
    [InlineData(-double.Epsilon, "-4.94065645841247E-324")]
    public void FormatsWithExplicitExtendedPrecisionConditions(double value, string expected)
    {
        Assert.Equal(expected, SpeechRecognitionNumberText.Format(value, '.'));
    }

    [Fact]
    public void NegativeZeroLosesItsSign()
    {
        double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negativeZero));
        Assert.Equal("0", SpeechRecognitionNumberText.Format(negativeZero, '.'));
    }

    [Theory]
    [InlineData(1.25, "1,25")]
    [InlineData(1.25e20, "1,25E20")]
    [InlineData(0.000125, "0,000125")]
    public void UsesTheSuppliedDecimalSeparator(double value, string expected)
    {
        Assert.Equal(expected, SpeechRecognitionNumberText.Format(value, ','));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsValuesOutsideTheFiniteRuntimeDomain(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechRecognitionNumberText.Format(value, '.'));
    }
}
