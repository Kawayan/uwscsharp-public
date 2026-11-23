using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// INIへ保存するウィンドウの外寸を物理ピクセルで保持します。
/// </summary>
public sealed class ApplicationWindowSize
{
    /// <summary>
    /// 幅と高さが正の整数であることを検査します。
    /// </summary>
    public ApplicationWindowSize(int widthPixels, int heightPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
    }

    /// <summary>
    /// 外寸の幅を物理ピクセルで取得します。
    /// </summary>
    public int WidthPixels
    {
        get;
    }

    /// <summary>
    /// 外寸の高さを物理ピクセルで取得します。
    /// </summary>
    public int HeightPixels
    {
        get;
    }

    internal string IniText => WidthPixels.ToString(CultureInfo.InvariantCulture)
        + "," + HeightPixels.ToString(CultureInfo.InvariantCulture);

    internal static ApplicationWindowSize? FromIni(string text)
    {
        string[] fields = text.Split(',');
        if (fields.Length < 2 || !TryRead(fields[0], out int width)
            || !TryRead(fields[1], out int height))
        {
            return null;
        }

        return new ApplicationWindowSize(width, height);
    }

    private static bool TryRead(string text, out int value)
    {
        value = 0;
        if (!double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
        {
            return false;
        }

        double truncated = Math.Truncate(number);
        if (truncated < 1 || truncated > int.MaxValue)
        {
            return false;
        }

        value = (int)truncated;
        return true;
    }
}
