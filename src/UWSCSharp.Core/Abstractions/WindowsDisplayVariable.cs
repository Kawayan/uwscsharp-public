namespace UWSCSharp.Core;

/// <summary>
/// 読取り時に取得する画面とマウスの情報です。
/// </summary>
public enum WindowsDisplayVariable
{
    /// <summary>
    /// マウスの物理スクリーンX座標です。
    /// </summary>
    MouseX,
    /// <summary>
    /// マウスの物理スクリーンY座標です。
    /// </summary>
    MouseY,
    /// <summary>
    /// 主画面の物理ピクセル幅です。
    /// </summary>
    ScreenWidth,
    /// <summary>
    /// 主画面の物理ピクセル高さです。
    /// </summary>
    ScreenHeight,
    /// <summary>
    /// 主画面の1ピクセル当たりのビット数です。
    /// </summary>
    ScreenBitsPerPixel
}

internal enum MonitorItem
{
    X = 0,
    Y = 1,
    Width = 2,
    Height = 3,
    Primary = 4,
    Name = 5
}

internal readonly record struct MonitorRequest(int Number, MonitorItem Item)
{
    internal static MonitorRequest? Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        if (arguments.Count is < 1 or > 2
            || arguments.Any(value => value.Kind == ValueKind.Omitted))
        {
            throw new InvalidOperationException(
                "MONITORにはモニター番号と任意の取得項目を指定してください。");
        }

        int number = ReadInteger(arguments[0]);
        int item = arguments.Count == 1 ? 0 : ReadInteger(arguments[1]);
        if (!Enum.IsDefined((MonitorItem)item))
        {
            throw new InvalidOperationException("MONITORの番号または取得項目が不正です。");
        }

        return new(number, (MonitorItem)item);
    }

    private static int ReadInteger(Value value)
    {
        double number = NumericOperations.Number(value);
        if (!double.IsFinite(number)
            || number < int.MinValue || number > int.MaxValue)
        {
            throw new InvalidOperationException("MONITORの数値が範囲外です。");
        }

        return (int)Math.Truncate(number);
    }
}
