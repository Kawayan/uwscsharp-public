using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class ComSession
{
    /// <summary>
    /// 既存のSTAと値の所有管理を使い、元の変数を変更せずに変換します。
    /// </summary>
    internal ValueTask<Value> ConvertVariantAsync(Value value, ushort type,
        CancellationToken token) => SubmitAsync(() =>
    {
        // 同じ型なら、参照型の情報やnull BSTRを含む元の値を保持します。
        // ANSIへの明示変換は、既存どおりCP932へそろえます。
        if (VariantTypeRequest.HasSameType(value, type)
            && (type != 256 || value.ByteText is { CodePage: 932 }))
        {
            return value;
        }

        if (type == 0)
        {
            return Value.Empty;
        }

        if (type == 1)
        {
            return new Value(ValueKind.Null);
        }

        bool logical = value.Kind == ValueKind.Number && value.IsLogical
            && value.Number is 0 or 1;

        // スクリプト論理値の1/0と、Native BOOLの-1/0を区別します。
        // 文字列化だけは、数値でなくTrue/Falseを生成します。
        object? source = logical && type is 8 or 256 or 258
            ? new ComVariantValue(11, value.Number != 0) : Unwrap(value);
        ushort nativeType = type is 256 or 258 ? (ushort)8 : type;
        Value converted = Wrap(ComSafeArray.ConvertElement(source, nativeType,
            flags: 2, allowVariantPassthrough: false), token: token);
        if (nativeType != 8)
        {
            return converted;
        }

        return type == 256
            ? new EncodedText(EncodedText.EncodingFor(932).GetBytes(converted.Text), 932).ToValue()
            : Value.String(converted.Text);
    }, token);
}
