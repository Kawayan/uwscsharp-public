using System.Buffers.Binary;
using System.Numerics;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeDllExtendedCodec
{
    internal const int Size = 10;
    private const int ExponentBias = 16383;
    private const ulong IntegerBit = 1UL << 63;

    internal static byte[] Encode(double number)
    {
        byte[] bytes = new byte[Size];
        Write(bytes, number);
        return bytes;
    }

    internal static byte[] EncodeArray(object?[] elements)
    {
        byte[] bytes = new byte[checked(elements.Length * Size)];
        for (int index = 0; index < elements.Length; index++)
        {
            var value = (NativeDllExtendedArgument)elements[index]!;
            Write(bytes.AsSpan(index * Size, Size), value.Number);
        }

        return bytes;
    }

    internal static object[] DecodeArray(ReadOnlySpan<byte> bytes, ushort controlWord)
    {
        if (bytes.Length % Size != 0)
        {
            throw new ArgumentException(
                "領域の長さがEXTENDEDの要素サイズの倍数ではありません。",
                nameof(bytes));
        }

        object[] elements = new object[bytes.Length / Size];
        for (int index = 0; index < elements.Length; index++)
        {
            elements[index] = Decode(bytes.Slice(index * Size, Size), controlWord);
        }

        return elements;
    }

    // doubleの仮数を明示整数ビット付きのx87形式に変換します。
    internal static void Write(Span<byte> bytes, double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentException("DLLのEXTENDED引数には有限値を指定してください。", nameof(number));
        }

        ulong bits = BitConverter.DoubleToUInt64Bits(number);
        int exponent = (int)((bits >> 52) & 0x7ff);
        ulong significand = bits & 0x000f_ffff_ffff_ffff;
        if (exponent != 0)
        {
            exponent += ExponentBias - 1023;
            significand = (significand | (1UL << 52)) << 11;
        }
        else if (significand != 0)
        {
            int shift = BitOperations.LeadingZeroCount(significand);
            exponent = ExponentBias - 1074 + 63 - shift;
            significand <<= shift;
        }

        ushort signedExponent = (ushort)(exponent | (int)((bits >> 48) & 0x8000));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, significand);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], signedExponent);
    }

    internal static double Decode(ReadOnlySpan<byte> bytes, ushort controlWord = 0)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException("DLLのEXTENDED値は10バイトで指定してください。", nameof(bytes));
        }

        ulong significand = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        ushort signedExponent = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        ulong sign = (ulong)(signedExponent & 0x8000) << 48;
        int encodedExponent = signedExponent & 0x7fff;
        int roundingMode = (controlWord >> 10) & 3;
        bool awayFromZero = roundingMode == (sign == 0 ? 2 : 1);
        if (encodedExponent == 0)
        {
            ulong fraction = awayFromZero && significand != 0 ? 1UL : 0;
            return BitConverter.UInt64BitsToDouble(sign | fraction);
        }
        if (significand < IntegerBit
            || (encodedExponent == 0x7fff && significand != IntegerBit))
        {
            return double.NaN;
        }

        int exponent = encodedExponent - ExponentBias;
        if (exponent > 1023)
        {
            ulong magnitude = encodedExponent == 0x7fff || roundingMode == 0
                || awayFromZero ? 0x7ff0_0000_0000_0000UL : 0x7fef_ffff_ffff_ffffUL;
            return BitConverter.UInt64BitsToDouble(sign | magnitude);
        }

        // 非正規数も元の64ビット仮数から一度だけ丸めます。
        int shift = Math.Max(11, -exponent - 1011);
        ulong rounded = RoundSignificand(
            significand, shift, roundingMode, awayFromZero);
        ulong bits = exponent >= -1022
            ? ((ulong)(exponent + 1022) << 52) + rounded
            : rounded;
        return BitConverter.UInt64BitsToDouble(sign | bits);
    }

    private static ulong RoundSignificand(ulong significand, int shift,
        int roundingMode, bool awayFromZero)
    {
        if (shift >= 64)
        {
            return awayFromZero || (roundingMode == 0
                && shift == 64 && significand > IntegerBit) ? 1UL : 0;
        }

        ulong truncated = significand >> shift;
        ulong remainder = significand & ((1UL << shift) - 1);
        if (roundingMode != 0)
        {
            return awayFromZero && remainder != 0 ? truncated + 1 : truncated;
        }

        ulong midpoint = 1UL << (shift - 1);
        return remainder > midpoint || (remainder == midpoint && (truncated & 1) != 0)
            ? truncated + 1 : truncated;
    }
}
