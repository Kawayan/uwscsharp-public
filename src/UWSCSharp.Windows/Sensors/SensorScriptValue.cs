using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// コピー済みのセンサー値を、元の型と整数精度を保って返します。
/// </summary>
internal static class SensorScriptValue
{
    internal static Value Convert(SensorQueryResult result) => !result.HasValue
        ? new Value(ValueKind.Empty) : result.Value switch
        {
            // NaNも取得結果として保持し、利用時の検査は既存の数値処理へ任せます。
            double number => new Value(ValueKind.Number, number) with
            {
                NativeVariantType = 5,
                NativeScalar = number
            },
            long integer => Value.Numeric(integer) with
            {
                NativeVariantType = 20,
                NativeScalar = integer
            },
            bool flag => Value.Logical(flag),
            string text => Value.String(text),
            _ => throw new InvalidOperationException("センサー値の型が不正です。")
        };
}
