using System.Globalization;
using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

/// <summary>
/// UWSC互換の数値変換と丸め処理を提供します。
/// </summary>
public static class NumericOperations
{
    /// <summary>
    /// 採用済みの数値関数の仕様に従い、値を有限doubleへ変換します。
    /// </summary>
    public static double RuntimeNumber(Value value)
    {
        // COMのVT_BOOLは符号付き16bit値として数値化します。
        if (value.Kind == ValueKind.Number
            && value.NativeVariantType == 11
            && value.GetUnchangedNativeScalar() is bool flag)
        {
            return flag ? -1 : 0;
        }

        if (value.Kind != ValueKind.String)
        {
            return Number(value);
        }

        if (TryFiniteText(value.Text, out double number))
        {
            return number;
        }

        throw new InvalidOperationException("数値に変換できません。");
    }

    internal static bool TryFiniteText(string text, out double number)
    {
        number = 0;
        if (!text.Contains('\0')
            && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) && double.IsFinite(parsed))
        {
            number = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 値を数値に変換します。
    /// </summary>
    public static double Number(Value value)
    {
        double number = value.Kind == ValueKind.String
            ? TryParse(value.Text, out double parsed, false) ? parsed
                : throw new InvalidOperationException("数値に変換できません。")
            : value.AsNumber();
        // 公開Value構築子を使うadapterからの値も有限性を検査します。
        return double.IsFinite(number) ? number
            : throw new InvalidOperationException("数値が有限範囲を超えました。");
    }

    /// <summary>
    /// 値を範囲内の整数に変換します。
    /// </summary>
    public static int Integer(Value value)
    {
        double n = Math.Truncate(Number(value));
        if (n < int.MinValue || n > int.MaxValue)
        {
            throw new InvalidOperationException("整数引数が範囲外です。");
        }

        return (int)n;
    }

    /// <summary>
    /// 文字列の数値への変換を試みます。
    /// </summary>
    public static bool TryParse(string text,
        out double number, bool predicate = true, bool trim = true)
    {
        if (trim)
        {
            text = StringOperations.Trim(text);
        }

        number = 0;
        if (text.Length is < 1 or > 64 || (text.Length > 32
            && (text.IndexOf('.') is < 0 or >= 32)))
        {
            return false;
        }

        if (text.StartsWith('$'))
        {
            if (text.Length < 2 || !uint.TryParse(
                text.AsSpan(1), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out uint hex))
            {
                return false;
            }

            number = unchecked((int)hex);
            return true;
        }

        if (!Regex.IsMatch(text,
            @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[Ee][+-]?[0-9]+)?$"))
        {
            return false;
        }

        int exponent = text.LastIndexOfAny(['e', 'E']);
        if (predicate && exponent >= 0 && text[exponent
            + 1] is not ('+' or '-'))
        {
            return false;
        }

        return double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    /// <summary>
    /// 指定桁数で数値を丸めます。
    /// </summary>
    public static double Round(double value, int digits)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException("数値が有限範囲を超えました。");
        }

        if (digits is < -20 or > 20)
        {
            throw new InvalidOperationException(
                "ROUNDの桁指定は-20～20です。");
        }

        if (digits is >= -15 and <= 0)
        {
            return Math.Round(value, -digits,
                MidpointRounding.ToEven);
        }

        double scale = Math.Pow(10, digits);
        // 指定単位がdoubleの刻みより細かい場合は、元の値を保ちます。
        // 不要な拡大による中間値のオーバーフローも防ぎます。
        if (Math.Abs(value) >= 1e16 * scale)
        {
            return value;
        }

        return Math.Round(value / scale,
            MidpointRounding.ToEven) * scale;
    }

    /// <summary>
    /// ROUNDの桁指定を0方向へ切り捨てて検査します。
    /// </summary>
    internal static int RoundDigits(Value value)
    {
        double digits = Math.Truncate(RuntimeNumber(value));
        if (digits is < -20 or > 20)
        {
            throw new InvalidOperationException("ROUNDの桁指定は-20～20です。");
        }

        return (int)digits;
    }

    /// <summary>
    /// 数値を切り上げます。
    /// </summary>
    public static double Ceiling(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException(
                "CEILには有限数を指定してください。");
        }

        return Math.Ceiling(value);
    }
}

/// <summary>
/// GUIとMCPに依存しない、共有または明示的なシードを持つ乱数の状態。
/// </summary>
public sealed class UwscRandom(uint seed)
{
    private readonly object gate = new();
    private uint state = seed;

    internal static UwscRandom Shared
    {
        get;
    } = new(
        unchecked((uint)Environment.TickCount));

    internal static int ConvertBound(Value value)
    {
        double bound = Math.Truncate(NumericOperations.RuntimeNumber(value));
        if (bound < int.MinValue || bound > int.MaxValue)
        {
            throw new InvalidOperationException(
                "RANDOMの上限がInt32範囲外です。");
        }

        return (int)bound;
    }

    /// <summary>
    /// 現在の乱数状態を更新し、指定範囲の乱数を返します。
    /// </summary>
    public double Next(int? bound = null)
    {
        lock (gate)
        {
            state = unchecked(state * 0x08088405 + 1);
            return bound is null ? state * (1.0
                / 4294967296.0)
                : unchecked((int)(((ulong)unchecked((uint)bound.Value)
                * state) >> 32));
        }
    }
}
