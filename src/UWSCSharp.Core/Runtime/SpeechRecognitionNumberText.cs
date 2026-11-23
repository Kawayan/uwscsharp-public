using System.Globalization;
using System.Numerics;

namespace UWSCSharp.Core;

/// <summary>
/// 有効桁64ビット・最近接偶数丸めの条件で認識語の数値表記を作ります。
/// </summary>
internal static class SpeechRecognitionNumberText
{
    private static readonly BigInteger BcdLimit = BigInteger.Pow(10, 18);
    private static readonly BigInteger PrecisionLimit = BigInteger.Pow(10, 15);
    private static readonly BinaryValue[] SmallPowers =
        [.. Enumerable.Range(0, 32).Select(Power)];
    private static readonly BinaryValue[] LargePowers =
        [.. Enumerable.Range(0, 11).Select(index => Power(index * 32))];

    internal static string Format(double value, char decimalSeparator)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                "認識語の数値には有限値を指定してください。");
        }

        if (value == 0)
        {
            return "0";
        }

        double magnitude = Math.Abs(value);
        int exponent = Math.ILogB(magnitude);
        int point = ((exponent * 0x4d10) >> 16) + 1;
        BinaryValue scaled = Scale(FromDouble(magnitude), 18 - point);
        BigInteger coefficient = RoundInteger(scaled.Numerator, scaled.Denominator);
        if (coefficient >= BcdLimit)
        {
            // 原版は整数化後に10で割り、BCD保存時にも最近接丸めを行います。
            BinaryValue reduced = RoundBinary(coefficient, 10);
            coefficient = RoundInteger(reduced.Numerator, reduced.Denominator);
            point++;
        }

        // 18桁のBCDから15桁へは、次の桁が5以上なら絶対値を繰り上げます。
        var digits = BigInteger.DivRem(coefficient, 1000, out BigInteger remainder);
        if (remainder >= 500)
        {
            digits++;
        }

        if (digits >= PrecisionLimit)
        {
            digits /= 10;
            point++;
        }

        string significant = digits.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        string text = Render(significant, point, decimalSeparator);
        return value < 0 ? "-" + text : text;
    }

    private static string Render(string digits, int point, char separator)
    {
        if (point is < -3 or > 15)
        {
            string mantissa = digits.Length == 1
                ? digits : digits[0] + separator.ToString() + digits[1..];
            return mantissa + "E" + (point - 1).ToString(CultureInfo.InvariantCulture);
        }

        if (point <= 0)
        {
            return "0" + separator + new string('0', -point) + digits;
        }

        return point >= digits.Length
            ? digits + new string('0', point - digits.Length)
            : digits[..point] + separator + digits[point..];
    }

    private static BinaryValue FromDouble(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7ff);
        ulong significand = bits & 0xfffffffffffff;
        int exponent = -1074;
        if (biased != 0)
        {
            significand |= 1UL << 52;
            exponent = biased - 1023 - 52;
        }

        return exponent >= 0
            ? new BinaryValue(new BigInteger(significand) << exponent, BigInteger.One)
            : new BinaryValue(significand, BigInteger.One << -exponent);
    }

    private static BinaryValue Scale(BinaryValue value, int exponent)
    {
        int magnitude = Math.Abs(exponent);
        value = Apply(value, SmallPowers[magnitude & 31], exponent < 0);
        int large = magnitude >> 5;
        return large == 0 ? value : Apply(value, LargePowers[large], exponent < 0);
    }

    private static BinaryValue Apply(BinaryValue value, BinaryValue factor, bool divide) =>
        divide
            ? RoundBinary(value.Numerator * factor.Denominator,
                value.Denominator * factor.Numerator)
            : RoundBinary(value.Numerator * factor.Numerator,
                value.Denominator * factor.Denominator);

    private static BinaryValue Power(int exponent) =>
        RoundBinary(BigInteger.Pow(10, exponent), BigInteger.One);

    private static BinaryValue RoundBinary(BigInteger numerator, BigInteger denominator)
    {
        int exponent = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        bool below = exponent >= 0
            ? numerator < (denominator << exponent)
            : (numerator << -exponent) < denominator;
        if (below)
        {
            exponent--;
        }

        int shift = 63 - exponent;
        BigInteger rounded = shift >= 0
            ? RoundInteger(numerator << shift, denominator)
            : RoundInteger(numerator, denominator << -shift);
        return shift >= 0
            ? new BinaryValue(rounded, BigInteger.One << shift)
            : new BinaryValue(rounded << -shift, BigInteger.One);
    }

    private static BigInteger RoundInteger(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int comparison = (remainder << 1).CompareTo(denominator);
        return comparison > 0 || (comparison == 0 && !quotient.IsEven)
            ? quotient + 1 : quotient;
    }

    // 正の二進有理数を保持し、ホストの浮動小数丸め設定に依存しません。
    private readonly record struct BinaryValue(BigInteger Numerator, BigInteger Denominator);
}
