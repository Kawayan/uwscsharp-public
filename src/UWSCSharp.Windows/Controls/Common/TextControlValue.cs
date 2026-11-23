namespace UWSCSharp.Windows;

/// <summary>
/// 取得した本文と、要求された場合の対象矩形です。
/// </summary>
internal readonly record struct TextControlValue(
    bool Found, string Text, Native.Rect? Bounds = null)
{
    internal static TextControlValue Missing => new(false, "");
}

/// <summary>
/// 文字列取得の対象内へ移動する物理座標を計算します。
/// </summary>
internal static class TextTargetGeometry
{
    internal static Native.Rect FromUia(double[] bounds)
    {
        if (bounds.Length != 4 || bounds.Any(value => !double.IsFinite(value))
            || bounds[2] < 0 || bounds[3] < 0)
        {
            throw Invalid();
        }

        return WindowsOperationErrors.Calculate(() => new Native.Rect
        {
            Left = checked((int)Math.Floor(bounds[0])),
            Top = checked((int)Math.Floor(bounds[1])),
            Right = checked((int)Math.Ceiling(bounds[0] + bounds[2])),
            Bottom = checked((int)Math.Ceiling(bounds[1] + bounds[3]))
        });
    }

    internal static Native.Point Inside(Native.Rect bounds)
    {
        long width = (long)bounds.Right - bounds.Left;
        long height = (long)bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            throw Invalid();
        }

        return new(checked(bounds.Left + (int)Math.Min(4, width - 1)),
            checked(bounds.Top + (int)Math.Min(4, height - 1)));
    }

    private static InvalidOperationException Invalid() =>
        WindowsOperationErrors.Record(
            new InvalidOperationException("取得対象の矩形が不正です。"));
}
