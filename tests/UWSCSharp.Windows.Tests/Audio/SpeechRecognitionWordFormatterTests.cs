using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 値の変換だけを使い、音声エンジン・外部COMサーバー・GUIは起動しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionWordFormatterTests
{
    [Theory]
    [InlineData(ValueKind.Empty, "")]
    [InlineData(ValueKind.Null, "NULL")]
    [InlineData(ValueKind.Nothing, "NOTHING")]
    [InlineData(ValueKind.Error, "ERROR")]
    public async Task FormatsAbsentAndErrorValuesWithoutNumericCoercion(ValueKind kind, string expected)
    {
        var formatter = new SpeechRecognitionWordFormatter('.');
        Assert.Equal(expected, await formatter.FormatAsync(new Value(kind), default));
    }

    [Fact]
    public async Task StringsKeepSpecialCharactersAndEmbeddedNull()
    {
        var formatter = new SpeechRecognitionWordFormatter('.');
        const string original = "<#_TAB>\0語\r\n";
        Assert.Equal(original, await formatter.FormatAsync(Value.String(original), default));
    }

    [Fact]
    public async Task ScriptBooleansUseBooleanTextWhileNumbersUseDigits()
    {
        var formatter = new SpeechRecognitionWordFormatter('.');
        Assert.Equal("True", await formatter.FormatAsync(Value.Logical(true), default));
        Assert.Equal("False", await formatter.FormatAsync(Value.Logical(false), default));
        Assert.Equal("1", await formatter.FormatAsync(Value.Numeric(1), default));
    }

    [Fact]
    public async Task NativeIntegersKeepPrecisionBeyondDouble()
    {
        var formatter = new SpeechRecognitionWordFormatter('.');
        Value signed = Value.Numeric(long.MaxValue) with
        {
            NativeVariantType = 20,
            NativeScalar = long.MaxValue
        };
        Value unsigned = Value.Numeric(ulong.MaxValue) with
        {
            NativeVariantType = 21,
            NativeScalar = ulong.MaxValue
        };
        Assert.Equal("9223372036854775807", await formatter.FormatAsync(signed, default));
        Assert.Equal("18446744073709551615", await formatter.FormatAsync(unsigned, default));
        Value unsignedByte = Value.Numeric(255) with
        {
            NativeVariantType = 17,
            NativeScalar = (byte)255
        };
        Assert.Equal("255", await formatter.FormatAsync(unsignedByte, default));
    }

    [Fact]
    public async Task ChangedNativeScalarUsesTheNewNumericValue()
    {
        var formatter = new SpeechRecognitionWordFormatter(',');
        Value changed = Value.Numeric(1) with
        {
            NativeVariantType = 11,
            NativeScalar = true,
            Number = 1.25
        };
        Assert.Equal("1,25", await formatter.FormatAsync(changed, default));
        Assert.Equal("123456789012345", await formatter.FormatAsync(
            Value.Numeric(123456789012344.5), default));
    }

    [Fact]
    public async Task DecimalUsesAutomationWhileChangedValueUsesNumericFormatting()
    {
        var formatter = new SpeechRecognitionWordFormatter();
        Value original = Value.Numeric((double)decimal.MaxValue) with
        {
            NativeVariantType = 14,
            NativeScalar = decimal.MaxValue
        };
        Assert.Equal("79228162514264337593543950335",
            await formatter.FormatAsync(original, default));
        Assert.Equal("1.25", await formatter.FormatAsync(
            original with
            {
                Number = 1.25
            }, default));
    }

    [Fact]
    public async Task UnsupportedValuesAndCancellationDoNotBecomeEmptyWords()
    {
        var formatter = new SpeechRecognitionWordFormatter('.');
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            formatter.FormatAsync(Value.FromObject(new object()), default).AsTask());
        Value unknown = Value.Nothing with
        {
            NativeVariantType = 13
        };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            formatter.FormatAsync(unknown, default).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            formatter.FormatAsync(Value.String("語"), cancellation.Token).AsTask());
    }
}
