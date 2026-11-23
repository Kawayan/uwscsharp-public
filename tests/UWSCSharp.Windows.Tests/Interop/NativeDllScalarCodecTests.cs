using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllScalarCodecTests
{
    public static TheoryData<object, string> ScalarCases => new()
    {
        { (sbyte)-128, "80" },
        { byte.MaxValue, "FF" },
        { short.MinValue, "0080" },
        { ushort.MaxValue, "FFFF" },
        { int.MinValue, "00000080" },
        { 0x12345678U, "78563412" },
        { long.MinValue, "0000000000000080" },
        { ulong.MaxValue, "FFFFFFFFFFFFFFFF" },
        { 1.5f, "0000C03F" },
        { -2.25d, "00000000000002C0" },
        { (nint)(-1), nint.Size == 8 ? "FFFFFFFFFFFFFFFF" : "FFFFFFFF" },
        { (nuint)0x12345678U, nuint.Size == 8 ? "7856341200000000" : "78563412" }
    };

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void ScalarBytesHaveExactWidthOrderAndBoxedType(object value, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        Assert.Equal(expected.Length, NativeDllScalarCodec.GetSize(value.GetType()));
        Assert.True(NativeDllScalarCodec.IsSupported(value.GetType()));
        Assert.Equal(expected, NativeDllScalarCodec.Encode(value));
        object decoded = NativeDllScalarCodec.Decode(value.GetType(), expected);
        Assert.Equal(value.GetType(), decoded.GetType());
        Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData("00000080")]
    [InlineData("0000807F")]
    [InlineData("000080FF")]
    [InlineData("3412C07F")]
    public void SingleBitsPreserveNegativeZeroInfinityAndNanPayload(string hex)
    {
        byte[] expected = Convert.FromHexString(hex);
        object value = NativeDllScalarCodec.Decode(typeof(float), expected);
        Assert.IsType<float>(value);
        Assert.Equal(expected, NativeDllScalarCodec.Encode(value));
    }

    [Theory]
    [InlineData("0000000000000080")]
    [InlineData("000000000000F07F")]
    [InlineData("000000000000F0FF")]
    [InlineData("341200000000F87F")]
    public void DoubleBitsPreserveNegativeZeroInfinityAndNanPayload(string hex)
    {
        byte[] expected = Convert.FromHexString(hex);
        object value = NativeDllScalarCodec.Decode(typeof(double), expected);
        Assert.IsType<double>(value);
        Assert.Equal(expected, NativeDllScalarCodec.Encode(value));
    }

    [Fact]
    public void ArrayUsesContiguousElementsWithoutPadding()
    {
        byte[] bytes = NativeDllScalarCodec.EncodeArray(typeof(short), [(short)-1, (short)2, short.MinValue]);
        Assert.Equal(Convert.FromHexString("FFFF02000080"), bytes);
        object[] values = NativeDllScalarCodec.DecodeArray(typeof(short), bytes);
        Assert.Equal<object>([(short)-1, (short)2, short.MinValue], values);
        Assert.All(values, value => Assert.IsType<short>(value));
        Assert.Empty(NativeDllScalarCodec.EncodeArray(typeof(int), []));
        Assert.Empty(NativeDllScalarCodec.DecodeArray(typeof(int), []));
    }

    [Theory]
    [InlineData(typeof(bool))]
    [InlineData(typeof(char))]
    [InlineData(typeof(string))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(void))]
    [InlineData(typeof(DayOfWeek))]
    public void UnsupportedTypesAreNotImplicitlyConverted(Type type)
    {
        Assert.False(NativeDllScalarCodec.IsSupported(type));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.GetSize(type));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.Decode(type, []));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.EncodeArray(type, []));
    }

    [Fact]
    public void WrongElementTypesNullAndMalformedLengthsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.EncodeArray(typeof(int), [1, 2L]));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.EncodeArray(typeof(int), [1, null]));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.Decode(typeof(int), new byte[3]));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.Decode(typeof(int), new byte[5]));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.DecodeArray(typeof(short), new byte[3]));
        Assert.Throws<ArgumentNullException>(() => NativeDllScalarCodec.Encode(null!));
        Assert.Throws<ArgumentException>(() => NativeDllScalarCodec.Encode(true));
    }
}
