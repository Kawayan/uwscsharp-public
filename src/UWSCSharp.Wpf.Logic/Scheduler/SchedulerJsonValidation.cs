using System.IO;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約のJSONで不正なUTF-8とサロゲートの置換を防ぎます。
/// </summary>
internal static class SchedulerJsonValidation
{
    internal static void Validate(ReadOnlySpan<byte> bytes, int maximumDepth)
    {
        new UTF8Encoding(false, true).GetCharCount(bytes);
        var reader = new Utf8JsonReader(bytes,
            new JsonReaderOptions { MaxDepth = maximumDepth });
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                ValidateSurrogates(reader.ValueSpan);
            }
        }
    }

    private static void ValidateSurrogates(ReadOnlySpan<byte> token)
    {
        for (int index = 0; index < token.Length; index++)
        {
            if (token[index] != '\\' || token[++index] != 'u')
            {
                continue;
            }

            int unit = Hex(token.Slice(index + 1, 4));
            index += 4;
            if (unit is >= 0xd800 and <= 0xdbff)
            {
                if (index + 6 >= token.Length || token[index + 1] != '\\'
                    || token[index + 2] != 'u' || Hex(token.Slice(index + 3, 4)) is < 0xdc00 or > 0xdfff)
                {
                    throw new InvalidDataException("予約設定のサロゲートが不正です。");
                }

                index += 6;
            }
            else if (unit is >= 0xdc00 and <= 0xdfff)
            {
                throw new InvalidDataException("予約設定のサロゲートが不正です。");
            }
        }
    }

    private static int Hex(ReadOnlySpan<byte> text)
    {
        int value = 0;
        foreach (byte character in text)
        {
            value = value * 16 + (character <= '9' ? character - '0'
                : (character | 0x20) - 'a' + 10);
        }

        return value;
    }

}
