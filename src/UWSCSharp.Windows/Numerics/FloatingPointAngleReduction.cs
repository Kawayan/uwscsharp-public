using System.Globalization;
using System.Numerics;

namespace UWSCSharp.Windows;

/// <summary>
/// 有限doubleの角度を象限と小さな剰余に分けます。
/// </summary>
internal static class FloatingPointAngleReduction
{
    private const int FractionBits = 2304;
    private const int SignificandBits = 53;

    // floor((π/2) × 2^2304)。最大の角度でも係数誤差を
    // doubleの最小刻みより小さく抑えるため、この幅を使います。
    private static readonly BigInteger HalfPi = BigInteger.Parse(
        "1921fb54442d18469898cc51701b839a252049c1114cf98e804177d4c7627364"
        + "4a29410f31c6809bbdf2a33679a748636605614dbe4be286e9fc26adadaa3848"
        + "bc90b6aecc4bcfd8de89885d34c6fdad617feb96de80d6fdbdc70d7f6b5133f4"
        + "b5d3e4822f8963fcc9250cca3d9c8b67b8400f97142c77e0b31b4906c38aba73"
        + "4d22c7f51fa499ebf06caba47b9475b2c38c5e6ac410aa5773daa520ee12d2cd"
        + "ace186a9c95793009e2e8d811943042f86520bc8c5c6d9c77c73cee58301d0c0"
        + "7364f0745d80f451f6b8abbe0de98a593bc5797ed2ab02e30732a92f9d52ad5c"
        + "a2ba44c3131f40a202ae51cb51555885b5a662e1a08a0f46750aa4357be3974c"
        + "9d9f70a08b1b7de1515d4e2aeba0c18fb672e1f0b4dc3c98f57eb5d19b61267a"
        + "e", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    internal static (int Quadrant, double High, double Low) Reduce(double angle)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(angle);
        ulong significand = (bits & ((1UL << 52) - 1)) | (1UL << 52);
        int exponent = (int)((bits >> 52) & 0x7ff) - 1023 - 52;
        var quotient = BigInteger.DivRem(
            new BigInteger(significand) << (FractionBits + exponent),
            HalfPi, out BigInteger rest);
        if (rest * 2 > HalfPi)
        {
            quotient++;
            rest -= HalfPi;
        }

        int quadrant = (int)(quotient & 3);
        if (angle < 0)
        {
            quadrant = (-quadrant) & 3;
            rest = -rest;
        }

        double high = TakePart(ref rest);
        double low = TakePart(ref rest);
        return (quadrant, high, low);
    }

    private static double TakePart(ref BigInteger scaled)
    {
        int sign = scaled.Sign;
        var magnitude = BigInteger.Abs(scaled);
        int shift = Math.Max((int)magnitude.GetBitLength() - SignificandBits, 0);
        long part = (long)(magnitude >> shift) * sign;
        scaled -= new BigInteger(part) << shift;
        return Math.ScaleB(part, shift - FractionBits);
    }
}
