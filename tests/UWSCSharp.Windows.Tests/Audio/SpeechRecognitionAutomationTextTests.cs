using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// Automationの値変換だけを使い、音声エンジン・GUI・外部COMサーバーは起動しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionAutomationTextTests
{
    [Theory]
    [InlineData(false, "False")]
    [InlineData(true, "True")]
    public void NativeBooleanKeepsAutomationText(bool flag, string expected)
    {
        Value value = Native(11, flag ? 1 : 0, flag);
        Assert.True(SpeechRecognitionAutomationText.TryFormat(value, out string text));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void CurrencyPreservesDigitsLostByTheDoubleProjection()
    {
        const decimal currency = 900719925474099.1234m;
        Value value = Native(6, (double)currency, currency);
        Assert.True(SpeechRecognitionAutomationText.TryFormat(value, out string text));
        Assert.StartsWith("900719925474099", text);
        Assert.EndsWith("1234", text);
        Assert.Equal(20, text.Length);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(14)]
    public void ManagedCultureDoesNotChangeUserDefaultLocaleConversion(int type)
    {
        var date = new DateTime(2026, 11, 5, 13, 24, 35);
        Value value = type switch
        {
            6 => Native(6, 1234.5678, 1234.5678m),
            7 => Native(7, date.ToOADate(), date),
            14 => Native(14, 1234.5678, 1234.5678m),
            _ => Native(11, 1, true)
        };
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.True(SpeechRecognitionAutomationText.TryFormat(value, out string first));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.True(SpeechRecognitionAutomationText.TryFormat(value, out string second));
            Assert.NotEmpty(first);
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DateTimeAndNativeDateDoubleUseTheSameDateValue()
    {
        var date = new DateTime(2026, 11, 5, 13, 24, 35);
        double number = date.ToOADate();
        Assert.True(SpeechRecognitionAutomationText.TryFormat(
            Native(7, number, date), out string fromDate));
        Assert.True(SpeechRecognitionAutomationText.TryFormat(
            Native(7, number, number), out string fromDouble));
        Assert.Equal(fromDate, fromDouble);
    }

    [Fact]
    public void ChangedScalarAndOrdinaryLogicalValuesAreNotNativeBooleans()
    {
        Value changed = Native(11, 1, true) with
        {
            Number = 0
        };
        Assert.False(SpeechRecognitionAutomationText.TryFormat(changed, out _));
        Assert.False(SpeechRecognitionAutomationText.TryFormat(Value.Logical(true), out _));
        Assert.False(SpeechRecognitionAutomationText.TryFormat(Value.String("語彙"), out _));
        Assert.False(SpeechRecognitionAutomationText.TryFormat(
            Native(5, 1.25, 1.25), out _));
    }

    [Fact]
    public void InvalidDatePreservesTheNativeFailureAndAllowsLaterConversion()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            SpeechRecognitionAutomationText.TryFormat(
                Native(7, double.MaxValue, double.MaxValue), out _));
        Assert.Equal(unchecked((int)0x80070057), failure.HResult);
        Assert.True(SpeechRecognitionAutomationText.TryFormat(
            Native(11, 1, true), out string text));
        Assert.Equal("True", text);
    }

    private static Value Native(ushort type, double number, object scalar) =>
        Value.Numeric(number) with
        {
            NativeVariantType = type,
            NativeScalar = scalar
        };

    [Fact]
    public void DecimalKeepsAllDigitsBeyondTheDoubleProjection()
    {
        foreach (decimal number in new[] { decimal.MaxValue, decimal.MinValue,
            0.0000000000000000000000000001m, -1234567890123456789.123456789m })
        {
            Assert.True(SpeechRecognitionAutomationText.TryFormat(
                Native(14, (double)number, number), out string text));
            decimal parsed = decimal.Parse(text, CultureInfo.CurrentCulture);
            Assert.Equal(number, parsed);
        }
    }

    [Fact]
    public void ChangedDecimalDoesNotReuseTheOriginalNativeScalar()
    {
        Value changed = Native(14, 12.5, 12.5m) with
        {
            Number = 13.5
        };
        Assert.False(SpeechRecognitionAutomationText.TryFormat(changed, out _));
    }
}
