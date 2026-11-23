using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllTextCodecTests
{
    public static TheoryData<int, int, string> UnicodeCases => new()
    {
        { 0x65e5, 0x672c, "E5652C670000" },
        { 0xd83d, 0xde00, "3DD800DE0000" },
        { 0xd800, 0x0041, "00D841000000" },
        { 0xdc00, 0x0041, "00DC41000000" }
    };

    [Fact]
    public void AnsiArgumentPreservesOriginalBytesInTheActiveCodePage()
    {
        int codePage = EncodedText.EncodingFor(0).CodePage;
        var source = new EncodedText([0x81, 0, 0xff], codePage);
        byte[] argument = NativeDllTextCodec.EncodeArgument(
            NativeDllPrimitive.Pchar, source.ToValue());

        Assert.Equal([0x81, 0, 0xff, 0], argument);
        Value result = NativeDllTextCodec.DecodeArgument(
            NativeDllPrimitive.Pchar, argument);
        Assert.Equal(source.CopyBytes(), result.ByteText?.CopyBytes());
    }

    [Fact]
    public void AnsiPreservesBytesAndSeparatesNulFromFixedLengthRead()
    {
        byte[] input = [0x82, 0xa0, 0, 0x82];
        byte[] bytes = NativeDllTextCodec.EncodeAnsi(input, 5);
        Assert.Equal([0x82, 0xa0, 0, 0x82, 0, 0], bytes);
        Array.Clear(input);
        Assert.Equal([0x82, 0xa0], NativeDllTextCodec.ReadAnsi(bytes, 5, true));
        byte[] fixedBytes = NativeDllTextCodec.ReadAnsi(bytes, 5, false);
        Assert.Equal([0x82, 0xa0, 0, 0x82, 0], fixedBytes);
        Array.Clear(fixedBytes);
        Assert.Equal(0x82, bytes[0]);
    }

    [Theory]
    [MemberData(nameof(UnicodeCases))]
    public void UnicodePreservesCodeUnits(int first, int second, string expectedHex)
    {
        string value = new([(char)first, (char)second]);
        byte[] bytes = NativeDllTextCodec.EncodeUnicode(value, value.Length);
        Assert.Equal(expectedHex, Convert.ToHexString(bytes));
        Assert.Equal(value, NativeDllTextCodec.ReadUnicode(bytes, value.Length, true));
        Assert.Equal(value, NativeDllTextCodec.ReadUnicode(bytes, value.Length, false));
    }

    [Fact]
    public void UnicodeOnlyRecognizesAlignedNulAndKeepsFixedLengthTail()
    {
        byte[] bytes = NativeDllTextCodec.EncodeUnicode("A\u0100\0B", 5);
        Assert.Equal("A\u0100", NativeDllTextCodec.ReadUnicode(bytes, 5, true));
        Assert.Equal("A\u0100\0B\0", NativeDllTextCodec.ReadUnicode(bytes, 5, false));
    }

    [Fact]
    public void EmptyCapacityStillHasOnlyTheTerminator()
    {
        Assert.Equal(new byte[1], NativeDllTextCodec.EncodeAnsi([], 0));
        Assert.Equal(new byte[2], NativeDllTextCodec.EncodeUnicode("", 0));
        Assert.Empty(NativeDllTextCodec.ReadAnsi(new byte[1], 0, true));
        Assert.Empty(NativeDllTextCodec.ReadUnicode(new byte[2], 0, false));
    }

    [Fact]
    public void MissingTerminatorFailsWithoutChangingFixedLengthRead()
    {
        byte[] ansi = [1, 2, 3];
        Assert.Throws<InvalidOperationException>(() => NativeDllTextCodec.ReadAnsi(ansi, 2, true));
        Assert.Equal([1, 2], NativeDllTextCodec.ReadAnsi(ansi, 2, false));
        byte[] wide = [65, 0, 66, 0];
        Assert.Throws<InvalidOperationException>(() => NativeDllTextCodec.ReadUnicode(wide, 1, true));
        Assert.Equal("A", NativeDllTextCodec.ReadUnicode(wide, 1, false));
    }

    [Fact]
    public void InvalidCapacityAndMismatchedBufferLengthsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDllTextCodec.EncodeAnsi([], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDllTextCodec.ReadUnicode([], -1, false));
        Assert.Throws<OverflowException>(() => NativeDllTextCodec.EncodeAnsi([], int.MaxValue));
        Assert.Throws<OverflowException>(() => NativeDllTextCodec.EncodeUnicode("", int.MaxValue / 2));
        Assert.Throws<ArgumentNullException>(() => NativeDllTextCodec.EncodeUnicode(null!, 1));
        Assert.Throws<ArgumentException>(() => NativeDllTextCodec.EncodeAnsi([1, 2], 1));
        Assert.Throws<ArgumentException>(() => NativeDllTextCodec.EncodeUnicode("\ud83d\ude00", 1));
        Assert.Throws<ArgumentException>(() => NativeDllTextCodec.ReadAnsi(new byte[2], 2, true));
        Assert.Throws<ArgumentException>(() => NativeDllTextCodec.ReadUnicode(new byte[3], 1, true));
        Assert.Throws<ArgumentException>(() => NativeDllTextCodec.ReadUnicode(new byte[6], 1, false));
    }
}
