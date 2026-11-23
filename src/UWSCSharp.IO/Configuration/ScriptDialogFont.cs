using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// INIへ保存するダイアログのフォント名と整数ポイント数です。
/// </summary>
public sealed class ScriptDialogFont
{
    /// <summary>
    /// フォント名と正の整数サイズを検査して保持します。
    /// </summary>
    public ScriptDialogFont(string family = "Tahoma", int sizePoints = 9)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        if (family.Contains(',') || family.Any(character => character < ' '))
        {
            throw new ArgumentException(
                "フォント名にカンマや改行などを指定できません。", nameof(family));
        }

        if (sizePoints <= 0 || sizePoints > int.MaxValue / 16)
        {
            throw new ArgumentOutOfRangeException(nameof(sizePoints),
                "文字サイズは1～134217727の整数で指定してください。");
        }

        Family = family.Trim();
        SizePoints = sizePoints;
    }

    /// <summary>
    /// 前後の空白を除いたフォント名を取得します。
    /// </summary>
    public string Family
    {
        get;
    }

    /// <summary>
    /// 1ポイントを1/72インチとする整数サイズを取得します。
    /// </summary>
    public int SizePoints
    {
        get;
    }

    internal string OptionText => Family + ","
        + SizePoints.ToString(CultureInfo.InvariantCulture);

    internal static ScriptDialogFont? FromIni(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        string[] fields = text.Split(',');
        string family = fields[0].Trim();
        if (family.Length == 0)
        {
            family = "Tahoma";
        }

        double points = fields.Length > 1
            && double.TryParse(fields[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed) ? Math.Truncate(parsed) : 9;
        if (points <= 0 || points > int.MaxValue / 16)
        {
            points = 9;
        }

        try
        {
            return new ScriptDialogFont(family, (int)points);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
