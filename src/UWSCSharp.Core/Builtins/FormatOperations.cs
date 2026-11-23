using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// FORMATの数値書式とUTF-16単位の文字列反復を処理します。
/// </summary>
internal static class FormatOperations
{
    internal const int MaximumWidth = 1_000_000;

    internal static string Format(Value input, Value widthValue, Value digitsValue)
    {
        int width = Integer(widthValue, 0, MaximumWidth);
        int digits = Integer(digitsValue, -1, 20);
        if (input.Kind == ValueKind.String)
        {
            string unit = input.Text;
            if (unit.Length == 0)
            {
                return "";
            }

            // 既存契約に従い、幅と切断位置はUTF-16のコード単位で扱います。
            return string.Create(width, unit, (destination, text) =>
            {
                for (int index = 0; index < destination.Length; index++)
                {
                    destination[index] = text[index % text.Length];
                }
            });
        }

        double number = NumericOperations.RuntimeNumber(input);
        string formatted;
        if (digits == -1)
        {
            double integer = Math.Truncate(number);
            if (integer < long.MinValue || integer >= 9223372036854775808d)
            {
                throw new InvalidOperationException(
                    "FORMATの16進入力がInt64範囲外です。");
            }

            formatted = ((long)integer).ToString("X", CultureInfo.InvariantCulture);
        }
        else
        {
            formatted = number.ToString("F" + digits, CultureInfo.InvariantCulture);
        }

        return formatted.PadLeft(width);
    }

    private static int Integer(Value value, int minimum, int maximum)
    {
        double number = Math.Truncate(NumericOperations.RuntimeNumber(value));
        if (number < minimum || number > maximum)
        {
            throw new InvalidOperationException(
                "FORMATの幅・桁数が範囲外です。");
        }

        return (int)number;
    }
}
