using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// 文字列関数で採用した有限数値の文字列化を共有します。
/// </summary>
internal static class BuiltinText
{
    internal static string Convert(Value input) => input.Kind == ValueKind.Number
        ? NumericOperations.RuntimeNumber(input).ToString(
            "G", CultureInfo.InvariantCulture)
        : input.Display();
}
