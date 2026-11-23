using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 文字コードとNLSによる文字変換だけを使い、画面・入力・focusは操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ByteTextOperationsTests
{
    private static Value S(string value) => Value.String(value);
    private static Value N(double value) => Value.Numeric(value);
    private static Value Call(string name, params Value[] args) => BuiltinFunctions.Evaluate(name, args);

    [Theory]
    [InlineData("", 0)]
    [InlineData("Aあｱ😀", 6)]
    [InlineData("A\0B", 3)]
    [InlineData("é", 1)]
    public void Cp932ByteLengthsUseLegacyFallback(string text, int length)
    {
        Assert.Equal(length, Call("LENGTHB", S(text)).Number);
    }

    [Theory]
    [InlineData(2, 1, "・", 130, 1)]
    [InlineData(3, 1, "\uf8f0", 160, 1)]
    [InlineData(2, 2, "あ", 130, 2)]
    [InlineData(0, 2, "A・", 65, 2)]
    [InlineData(99, 2, "", 0, 0)]
    [InlineData(1, -1, "", 0, 0)]
    public void CopyRetainsRawBytesIncludingSplitCharacters(int start, int length,
        string expected, int first, int byteLength)
    {
        Value copy = Call("COPYB", S("AあB"), N(start), N(length));
        Assert.Equal(expected, copy.Text);
        Assert.Equal(first, Call("ASCB", copy).Number);
        Assert.Equal(byteLength, Call("LENGTHB", copy).Number);
    }

    [Theory]
    [InlineData(0, "\0")]
    [InlineData(130, "・")]
    [InlineData(160, "\uf8f0")]
    [InlineData(255, "\uf8f3")]
    [InlineData(256, "\0")]
    [InlineData(-1, "\uf8f3")]
    [InlineData(33440, "\uf8f0")]
    public void ByteCharacterUsesLowByte(int code, string expected)
    {
        Value result = Call("CHRB", N(code));
        Assert.Equal(expected, result.Text);
        Assert.Equal((byte)code, Call("ASCB", result).Number);
        Assert.Equal(1, Call("LENGTHB", result).Number);
    }

    [Theory]
    [InlineData("B", "AあB", 1, 4)]
    [InlineData("あ", "あXあ", -1, 4)]
    [InlineData("A", "但A", 1, 3)]
    [InlineData("a", "但A", 1, 3)]
    [InlineData("aa", "aaa", 2, 2)]
    [InlineData("a", "aaa", 0, 1)]
    [InlineData("a", "aaa", -2, 2)]
    [InlineData("", "abc", 1, 0)]
    [InlineData("a", "aaa", 99, 0)]
    [InlineData("B", "A\0B", 1, 0)]
    public void PositionUsesAnsiCharacterBoundariesAndByteOffsets(
        string needle, string text, int occurrence, int expected)
    {
        Assert.Equal(expected, Call("POSB", S(needle), S(text), N(occurrence)).Number);
    }

    [Fact]
    public void PositionDoesNotSearchTrailBytesAndRespectsSameString()
    {
        Assert.Equal(0, Call("POSB", Call("CHRB", N(160)), S("あ")).Number);
        Assert.Equal(0, Call("POSB", Call("CHRB", N(130)), S("あ")).Number);
        Assert.Equal(0, BuiltinFunctions.Evaluate("POSB", [S("a"), S("A")], true).Number);
        Assert.Equal(1, Call("POSB", S("ａ"), S("Ａ")).Number);
    }

    [Theory]
    [InlineData("ＡBcあアｱ", 256, "ａbcあアｱ")]
    [InlineData("アｶﾞあＡ", 1048576, "あｶﾞあＡ")]
    [InlineData("あｶﾞア", 2097152, "アｶﾞア")]
    [InlineData("ＡＢＣ １２３ガあ。", 4194304, "ABC 123ｶﾞあ｡")]
    [InlineData("ABC 123ｶﾞ｡", 8388608, "ＡＢＣ　１２３ガ。")]
    [InlineData("AbCが", 6291968, "ABCｶﾞ")]
    [InlineData("abc", 0, "abc")]
    public void ConversionMatchesJapaneseNlsFlags(string text, int mode, string expected)
    {
        Assert.Equal(expected, Call("STRCONV", S(text), N(mode)).Text);
    }

    [Theory]
    [InlineData(768)]
    [InlineData(3145728)]
    [InlineData(12582912)]
    [InlineData(1)]
    [InlineData(-1)]
    public void ConversionRejectsConflictsAndUnknownFlags(int mode)
    {
        Assert.Throws<InvalidOperationException>(() => Call("STRCONV", S("abc"), N(mode)));
    }

    [Theory]
    [InlineData("Aあ😀\0B")]
    [InlineData("")]
    public void Utf8RetainsBytesAndExplicitDecodeReturnsUnicode(string text)
    {
        Value encoded = Call("ENCODE", S(text), N(3));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), Call("LENGTHB", encoded).Number);
        Assert.Equal(text, encoded.Text);
        Assert.Equal(text, Call("DECODE", encoded, N(3)).Text);
        Assert.Null(Call("DECODE", encoded, N(3)).ByteText);
    }

    [Fact]
    public async Task EncodedUtf8SurvivesAssignmentConcatenationAndEval()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            DIM v=ENCODE("あ",CODE_UTF8)
            PRINT LENGTHB(v)
            PRINT ASCB(COPYB(v,1,1))
            PRINT ASCB(COPYB(v,2,1))
            PRINT ASCB(COPYB(v,3,1))
            PRINT POSB("あ",v)
            PRINT LENGTHB(v+v)
            PRINT EVAL("DECODE(v,CODE_UTF8)")
            """, output.Add);
        Assert.Equal(["3", "227", "129", "130", "0", "6", "あ"], output);
    }

    [Theory]
    [InlineData("Aあ\0B", 5, 5)]
    [InlineData("Aあ😀\0B", 6, 12)]
    [InlineData("", 5, 0)]
    [InlineData("", 6, 0)]
    public void TypedByteArrayRoundTripsRetainUi1AndBounds(string text, int mode, int length)
    {
        Value encoded = Call("ENCODE", S(text), N(mode));
        Assert.Equal((ushort)17, encoded.AsArray().NativeElementType);
        Assert.Equal(1, encoded.AsArray().Rank);
        Assert.Equal(0, encoded.AsArray().LowerBounds[0]);
        Assert.Equal(length, encoded.AsArray().Count);
        Assert.Equal(text, Call("DECODE", encoded, N(mode)).Text);
    }

    [Fact]
    public void Utf16ArrayPreservesLoneSurrogateCodeUnits()
    {
        const string text = "x\ud800y\udc00";
        Assert.Equal(text, Call("DECODE", Call("ENCODE", S(text), N(6)), N(6)).Text);
    }

    [Fact]
    public void DecodeValidatesTypedArraysWithoutChangingThem()
    {
        Value original = Call("ENCODE", S("A"), N(5));
        foreach (Value invalid in new[] { S("65"), N(-1), N(256), N(1.1), new Value(ValueKind.Null) })
        {
            var changed = Value.FromArray(original.AsArray().Set([0], invalid));
            Assert.Throws<InvalidOperationException>(() => Call("DECODE", changed, N(5)));
            Assert.Equal(invalid, changed.AsArray().Get([0]));
        }

        Assert.Throws<InvalidOperationException>(() => Call("DECODE", original, N(6)));
        Assert.Throws<InvalidOperationException>(() => Call("DECODE",
            Value.FromArray(UwscArray.FromValues([N(65)])), N(5)));
        Assert.Equal(65, original.AsArray().Get([0]).Number);
    }

    [Fact]
    public void ByteArrayAllocationHonorsExistingElementLimit()
    {
        Assert.Throws<InvalidOperationException>(() => Call("ENCODE",
            S(new string('a', UwscArray.MaximumElements + 1)), N(5)));
    }

    [Theory]
    [InlineData("あ A+B/~", "%82%A0+A%2BB%2F%7E")]
    [InlineData("-_.!*'()~", "-_.!*'()%7E")]
    [InlineData("\0", "%00")]
    public void UrlUsesCp932FormEncoding(string text, string expected)
    {
        Value encoded = Call("ENCODE", S(text), N(2));
        Assert.Equal(expected, encoded.Text);
        Assert.Equal(text, Call("DECODE", encoded, N(2)).Text);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%Q1")]
    [InlineData("%1")]
    public void MalformedUrlIsAScriptError(string text)
    {
        Assert.Throws<InvalidOperationException>(() => Call("DECODE", S(text), N(2)));
    }

    [Fact]
    public void HtmlUsesTheFourPublishedEntitiesAndPreservesUnknownEntities()
    {
        Assert.Equal("&lt;&amp;&gt;&quot;'", Call("ENCODE", S("<&>\"'"), N(4)).Text);
        Assert.Equal("<&>\"&apos;Aあ", Call("DECODE",
            S("&lt;&amp;&gt;&quot;&apos;&#65;&#x3042;"), N(4)).Text);
        Assert.Equal("&unknown;&#xD800;&#999999999999;", Call("DECODE",
            S("&unknown;&#xD800;&#999999999999;"), N(4)).Text);
    }

    [Fact]
    public async Task MalformedUtf8AndLoneSurrogatesUseOriginalReplacement()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PRINT DECODE(CHRB(255),CODE_UTF8)
            DIM value=ENCODE(CHR(55296),CODE_UTF8)
            PRINT value
            PRINT LENGTHB(value)
            """, output.Add);
        Assert.Equal(["\ufffd", "\ufffd", "3"], output);
    }

    [Theory]
    [InlineData("COPYB(1/0)")]
    [InlineData("POSB(1/0)")]
    [InlineData("LENGTHB(1/0,2)")]
    [InlineData("ASCB(1/0,2)")]
    [InlineData("CHRB(1/0,2)")]
    [InlineData("STRCONV(1/0)")]
    [InlineData("ENCODE(1/0)")]
    [InlineData("DECODE(1/0)")]
    [InlineData("CPUUSERATE(1/0)")]
    public async Task ArityIsCheckedBeforeArgumentEvaluation(string expression)
    {
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("PRINT " + expression, _ => { }));
        Assert.Contains("引数", failure.Message);
        Assert.DoesNotContain("ゼロ", failure.Message);
    }

    [Fact]
    public void ChangedPublicValueKindOrTextDoesNotReuseEncodedBytes()
    {
        Value encoded = Call("ENCODE", S("あ"), N(3));
        Value number = encoded with
        {
            Kind = ValueKind.Number,
            Number = 7
        };
        Assert.Equal(1, Call("LENGTHB", number).Number);
        Assert.Equal(55, Call("ASCB", number).Number);
        Assert.Equal("7あ", Expression.Apply("+", number, encoded).Text);
        Assert.Equal(1, Call("LENGTHB", encoded with
        {
            Text = "x"
        }).Number);
    }

}
