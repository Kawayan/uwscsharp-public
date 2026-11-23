using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 借用したバイト列だけを読み、COM・機器・画面・入力には触れません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorNumericValueTests
{
    [Theory]
    [InlineData(VarEnum.VT_I1, 0x80UL, -128d)]
    [InlineData(VarEnum.VT_UI1, 0xffUL, 255d)]
    [InlineData(VarEnum.VT_I2, 0x8000UL, -32768d)]
    [InlineData(VarEnum.VT_UI2, 0xffffUL, 65535d)]
    [InlineData(VarEnum.VT_I4, 0x80000000UL, -2147483648d)]
    [InlineData(VarEnum.VT_UI4, 0xffffffffUL, 4294967295d)]
    [InlineData(VarEnum.VT_I8, 0x8000000000000000UL, -9223372036854775808d)]
    [InlineData(VarEnum.VT_UI8, ulong.MaxValue, 18446744073709551616d)]
    [InlineData(VarEnum.VT_R4, 0xbfa00000UL, -1.25d)]
    [InlineData(VarEnum.VT_R8, 0xc004000000000000UL, -2.5d)]
    public void NumericTypesUseTheirNativeWidthAndSignedness(
        VarEnum type, ulong bits, double expected)
    {
        byte[] bytes = Property(type, bits);
        // 予約欄とx64の後方領域を、数値へ混入させないことも確認します。
        bytes.AsSpan(2, 6).Fill(0xff);
        bytes.AsSpan(16).Fill(0xff);
        Assert.Equal(expected, SensorNumericValue.Read(bytes));
    }

    [Theory]
    [InlineData(VarEnum.VT_EMPTY)]
    [InlineData(VarEnum.VT_BOOL)]
    [InlineData(VarEnum.VT_DATE)]
    [InlineData(VarEnum.VT_LPWSTR)]
    [InlineData(VarEnum.VT_ERROR)]
    [InlineData(VarEnum.VT_INT)]
    [InlineData(VarEnum.VT_UINT)]
    [InlineData(VarEnum.VT_ARRAY | VarEnum.VT_R8)]
    [InlineData(VarEnum.VT_BYREF | VarEnum.VT_R8)]
    [InlineData(VarEnum.VT_VECTOR | VarEnum.VT_R8)]
    public void OtherTypesReturnNegativeQuietNaNWithoutFollowingPointers(VarEnum type)
    {
        double result = SensorNumericValue.Read(Property(type, ulong.MaxValue));
        Assert.Equal(unchecked((long)0xfff8000000000000),
            BitConverter.DoubleToInt64Bits(result));
    }

    [Theory]
    [InlineData(0x8000000000000000UL)]
    [InlineData(0x7ff0000000000000UL)]
    [InlineData(0xfff0000000000000UL)]
    [InlineData(0x7ff8000000000042UL)]
    public void DoubleDoesNotReplaceNegativeZeroInfinityOrQuietNaN(ulong bits)
    {
        double result = SensorNumericValue.Read(Property(VarEnum.VT_R8, bits));
        Assert.Equal(bits, unchecked((ulong)BitConverter.DoubleToInt64Bits(result)));
    }

    [Fact]
    public void Unsigned64BitRoundingKeepsTheEvenDoubleAtTheHalfwayPoint()
    {
        Assert.Equal(9007199254740992d, SensorNumericValue.Read(
            Property(VarEnum.VT_UI8, 9007199254740993)));
        Assert.Equal(9007199254740996d, SensorNumericValue.Read(
            Property(VarEnum.VT_UI8, 9007199254740995)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    public void TruncatedStorageIsRejectedBeforeReadingTheUnion(int length) =>
        Assert.Throws<ArgumentException>(() => SensorNumericValue.Read(new byte[length]));

    private static byte[] Property(VarEnum type, ulong bits)
    {
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)type);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), bits);
        return bytes;
    }
}
