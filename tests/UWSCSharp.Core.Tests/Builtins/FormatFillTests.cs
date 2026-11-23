using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class FormatFillTests
{
    [Theory]
    [InlineData("ab", 5, "ababa")]
    [InlineData("abcdef", 2, "ab")]
    [InlineData("", 10, "")]
    [InlineData("abc", 0, "")]
    [InlineData("😀", 2, "😀")]
    [InlineData("123", 4, "1231")]
    public void StringFillRepeatsAndCutsUtf16Units(string unit, int width, string expected)
    {
        Value result = BuiltinFunctions.Evaluate("FORMAT",
            [Value.String(unit), Value.Numeric(width)]);
        Assert.Equal(expected, result.Display());
    }

    [Theory]
    [InlineData("😀", 3, new[] { 0xd83d, 0xde00, 0xd83d })]
    [InlineData("A😀", 5, new[] { 0x41, 0xd83d, 0xde00, 0x41, 0xd83d })]
    public void OddWidthRetainsTheTruncatedSurrogateCodeUnit(string unit, int width,
        int[] expected)
    {
        string text = BuiltinFunctions.Evaluate("FORMAT",
            [Value.String(unit), Value.Numeric(width)]).Display();
        Assert.Equal(expected, text.Select(character => (int)character));
    }

    [Fact]
    public void MaximumOutputLengthIsBoundedWithoutBuildingFullRepeatedInput()
    {
        const int maximumWidth = 1_000_000;
        string text = BuiltinFunctions.Evaluate("FORMAT",
            [Value.String("ab"), Value.Numeric(maximumWidth)]).Display();
        Assert.Equal(maximumWidth, text.Length);
        Assert.StartsWith("abab", text);
        Assert.EndsWith("abab", text);
        Assert.Throws<InvalidOperationException>(() => BuiltinFunctions.Evaluate("FORMAT",
            [Value.String("ab"), Value.Numeric(maximumWidth + 1)]));
    }
}
