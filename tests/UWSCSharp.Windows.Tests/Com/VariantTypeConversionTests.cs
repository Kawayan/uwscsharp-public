using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// ウィンドウ・入力・外部COMサーバーを使わず、STA内の値変換だけを確認します。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class VariantTypeConversionTests
{
    [Theory]
    [InlineData(2, "2.5", "2")]
    [InlineData(3, "3.5", "4")]
    [InlineData(4, "1.25", "1.25")]
    [InlineData(5, "1.25", "1.25")]
    [InlineData(6, "1.23456", "1.2346")]
    [InlineData(7, "2026/11/03", "2026-11-03T00:00:00")]
    [InlineData(11, "True", "True")]
    [InlineData(16, "-2.5", "-2")]
    [InlineData(17, "255", "255")]
    [InlineData(18, "65535", "65535")]
    [InlineData(19, "4294967295", "4294967295")]
    [InlineData(20, "9007199254740993", "9007199254740993")]
    [InlineData(21, "18446744073709551615", "18446744073709551615")]
    public async Task ConvertsWithNativeTypeAndPrecision(int type, string source, string expected)
    {
        await using var session = new ComSession();
        var original = Value.String(source);
        Value result = await session.ConvertVariantAsync(original, (ushort)type, default);
        Assert.Equal(type, VariantTypeRequest.Query(result).Number);
        Assert.Equal(expected, new PrintFormatter().Format(result));
        Assert.Equal(source, original.Text);
        Assert.Equal(258, VariantTypeRequest.Query(original).Number);
    }

    [Theory]
    [InlineData(8, 258, "Aあ😀")]
    [InlineData(256, 256, "Aあ??")]
    [InlineData(258, 258, "Aあ😀")]
    public async Task PreservesTheDistinctionBetweenAnsiAndUnicode(int target, int type, string expected)
    {
        await using var session = new ComSession();
        Value result = await session.ConvertVariantAsync(Value.String("Aあ😀"), (ushort)target, default);
        Assert.Equal(type, VariantTypeRequest.Query(result).Number);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(13, 13)]
    public async Task ConvertsAbsentValuesWithoutCreatingAnObject(int target, int expected)
    {
        await using var session = new ComSession();
        Value result = await session.ConvertVariantAsync(Value.Empty, (ushort)target, default);
        Assert.Equal(expected, VariantTypeRequest.Query(result).Number);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(17)]
    public async Task ConversionErrorsRetainTheirScriptClassification(int target)
    {
        await using var session = new ComSession();
        var source = Value.Numeric(target == 17 ? 256 : 123);
        Exception error = await Assert.ThrowsAnyAsync<Exception>(
            () => session.ConvertVariantAsync(source, (ushort)target, default).AsTask());
        Assert.NotNull(session.GetScriptErrorMessage(error));
        Assert.Equal(target == 17 ? 256 : 123, source.Number);
    }

    [Fact]
    public async Task LogicalInputUsesBooleanCoercionWithoutChangingExistingArithmetic()
    {
        await using var session = new ComSession();
        Value logical = ExpressionEvaluator.Evaluate("TRUE");
        Value result = await session.ConvertVariantAsync(logical, 3, default);
        Assert.Equal(1, result.Number);
        Assert.Equal(1, NumericOperations.RuntimeNumber(logical));
        Value text = await session.ConvertVariantAsync(logical, 258, default);
        Assert.Equal("True", text.Text);
    }

    [Fact]
    public async Task ScriptAndNativeLogicalPayloadsKeepTheirDifferentNumericValues()
    {
        await using var session = new ComSession();
        Value logical = ExpressionEvaluator.Evaluate("TRUE");
        Value unchanged = await session.ConvertVariantAsync(logical, 11, default);
        Assert.Equal(11, VariantTypeRequest.Query(unchanged).Number);
        Assert.Equal(1, (await session.ConvertVariantAsync(unchanged, 3, default)).Number);
        Assert.Equal(1, (await session.ConvertVariantAsync(logical, 5, default)).Number);
        foreach (Value source in new[] { Value.Numeric(1), Value.String("True") })
        {
            Value native = await session.ConvertVariantAsync(source, 11, default);
            Assert.Equal(11, VariantTypeRequest.Query(native).Number);
            Assert.Equal(-1, (await session.ConvertVariantAsync(native, 3, default)).Number);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task CancellationIsObservedBeforeConversion(int type)
    {
        await using var session = new ComSession();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ConvertVariantAsync(Value.Numeric(1), (ushort)type, stop.Token).AsTask());
    }

    [Fact]
    public async Task AnsiIdentityStillTranscodesOtherByteTextEncodingsToCp932()
    {
        await using var session = new ComSession();
        var source = new EncodedText(Encoding.UTF8.GetBytes("Aあ😀"), 65001).ToValue();
        Assert.Equal(256, VariantTypeRequest.Query(source).Number);
        Value result = await session.ConvertVariantAsync(source, 256, default);
        Assert.Equal(932, result.ByteText!.CodePage);
        Assert.Equal("Aあ??", result.Text);
        Assert.Equal(65001, source.ByteText!.CodePage);
        Assert.Equal("Aあ😀", source.Text);
    }

    [Theory]
    [InlineData("ja-JP", "1.25")]
    [InlineData("en-US", "1.25")]
    [InlineData("de-DE", "1,25")]
    public async Task NativeConversionUsesTheSessionCulture(string culture, string text)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using var session = new ComSession();
            Value number = await session.ConvertVariantAsync(Value.String(text), 5, default);
            Assert.Equal(1.25, number.Number);
            Assert.Equal(5, VariantTypeRequest.Query(number).Number);
            Value result = await session.ConvertVariantAsync(Value.Numeric(1.25), 258, default);
            Assert.Equal(text, result.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
