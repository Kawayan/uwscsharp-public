using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// ログ表示に用いるローカルフォント名とポイント数です。
/// </summary>
public sealed class ScriptLogFont
{
    /// <summary>
    /// 既定値または指定されたフォント名とサイズを保持します。
    /// </summary>
    public ScriptLogFont(string family = "ＭＳ ゴシック",
        double sizePoints = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        if (family.IndexOfAny(['#', '/', '\\', ':', ',']) >= 0
            || family.Any(char.IsControl))
        {
            throw new ArgumentException(
                "ローカルのフォント名を指定してください。", nameof(family));
        }

        if (!double.IsFinite(sizePoints) || sizePoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizePoints));
        }

        Family = family.Trim();
        SizePoints = sizePoints;
    }

    /// <summary>
    /// ローカルフォント名を取得します。
    /// </summary>
    public string Family
    {
        get;
    }

    /// <summary>
    /// 1ポイントを1/72インチとするサイズを取得します。
    /// </summary>
    public double SizePoints
    {
        get;
    }

    internal static ScriptLogFont FromIni(string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])
            || !double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double size)
            || !double.IsFinite(size) || size <= 0)
        {
            return new ScriptLogFont();
        }

        try
        {
            return new ScriptLogFont(parts[0], size);
        }
        catch (ArgumentException)
        {
            return new ScriptLogFont();
        }
    }
}
