using System.Globalization;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識語の基本値と、元型を保持したscalarの文字列化をまとめます。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SpeechRecognitionWordFormatter(char decimalSeparator = '.')
{
    internal ValueTask<string> FormatAsync(Value value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string text = value.Kind switch
        {
            ValueKind.Empty => string.Empty,
            ValueKind.Null => "NULL",
            ValueKind.Nothing when value.NativeVariantType is null or 9 => "NOTHING",
            ValueKind.Error => "ERROR",
            ValueKind.String => value.Text,
            ValueKind.Number => FormatNumber(value),
            _ => throw new NotSupportedException(
                "この型の認識語への文字列化は未対応です。")
        };
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(text);
    }

    private string FormatNumber(Value value)
    {
        _ = value.AsNumber();
        if (value.IsLogical && value.Number is 0 or 1)
        {
            // スクリプトの論理値にも、型照会と同じBOOLの変換経路を使います。
            value = value with
            {
                NativeVariantType = 11,
                NativeScalar = value.Number != 0
            };
        }

        if (SpeechRecognitionAutomationText.TryFormat(value, out string text))
        {
            return text;
        }

        object? scalar = value.GetUnchangedNativeScalar();
        if (scalar is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            return ((IFormattable)scalar).ToString(null, CultureInfo.InvariantCulture);
        }

        if (scalar is null or float or double)
        {
            return SpeechRecognitionNumberText.Format(value.Number, decimalSeparator);
        }

        throw new NotSupportedException("このnative型の認識語への文字列化は未対応です。");
    }
}
