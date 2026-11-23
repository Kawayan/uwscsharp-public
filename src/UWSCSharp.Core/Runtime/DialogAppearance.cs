using System.Globalization;

namespace UWSCSharp.Core;

/// <summary>
/// 次のダイアログへ適用する変更不可の表示設定です。
/// </summary>
internal sealed record DialogAppearance(string Title, string FontFamily = "Tahoma",
    double FontSize = 9, int? X = null, int? Y = null,
    bool FixedBalloon = false)
{
    internal DialogAppearance Apply(string name, IReadOnlyList<Value> values)
    {
        if (name == Keywords.FixBalloon)
        {
            if (values.Count != 0)
            {
                throw new InvalidOperationException("FIXBALLOONには値を指定しないでください。");
            }

            return this with
            {
                FixedBalloon = true
            };
        }

        if (values.Count != (name == Keywords.Position ? 2 : 1))
        {
            throw new InvalidOperationException("ダイアログ設定の値の数が不正です。");
        }

        if (name == Keywords.DlgTitle)
        {
            return this with
            {
                Title = BuiltinText.Convert(values[0])
            };
        }

        if (name == Keywords.Position)
        {
            var position = ScriptWindowPosition.Read(values);
            return this with
            {
                X = position.X,
                Y = position.Y
            };
        }

        if (name == Keywords.DefaultFont)
        {
            string text = BuiltinText.Convert(values[0]);
            int separator = text.LastIndexOf(',');
            string family = (separator < 0 ? text : text[..separator]).Trim();
            double size = separator < 0 ? FontSize :
                double.TryParse(text[(separator + 1)..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN;
            if (family.Length == 0 || family.Any(character => character < ' ')
                || !double.IsFinite(size) || size <= 0 || size > int.MaxValue / 16)
            {
                throw new InvalidOperationException("DEFAULTFONTにはフォント名と正の有限サイズを指定してください。");
            }

            return this with
            {
                FontFamily = family,
                FontSize = size
            };
        }

        throw new InvalidOperationException("未対応のダイアログ設定です。");
    }
}
