namespace UWSCSharp.Core;

/// <summary>
/// 吹出しの本文と省略を保持した表示指定です。
/// </summary>
internal sealed record BalloonRequest(
    string Message, int? X, int? Y, int? Direction,
    double? FontSize, string? FontFamily,
    int? TextColor, int? BackgroundColor, int? Transparency)
{
    internal bool Hidden => Message.Length == 0;

    // 背景透過の指定は、全体のalphaとは別に扱います。
    internal byte? Alpha => Transparency is >= 0
        ? (byte)(255 - Transparency.Value) : null;

    internal static BalloonRequest Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count > 9
            || (arguments.Count > 0 && arguments[0].Kind == ValueKind.Omitted))
        {
            throw new InvalidOperationException(
                "吹出しには本文と省略可能な8引数を指定するか、引数なしで消去してください。");
        }

        var args = new CallArguments(arguments);
        string message = arguments.Count == 0
            ? "" : BuiltinText.Convert(arguments[0]);
        if (message.Contains('\0'))
        {
            throw new InvalidOperationException("吹出しの本文にNULは指定できません。");
        }

        int? x = Integer(1);
        int? y = Integer(2);
        int? direction = Integer(3);
        if (direction is < 0 or > 4)
        {
            throw new InvalidOperationException("吹出しの向きは0～4で指定してください。");
        }

        double? size = args.Has(4)
            ? NumericOperations.RuntimeNumber(args.Get(4)) : null;
        if (size is > int.MaxValue / 16)
        {
            throw new InvalidOperationException(
                "吹出しのフォントサイズが範囲外です。");
        }

        size = size.HasValue ? Math.Truncate(size.Value) : null;
        size = size switch
        {
            0 => null,
            < 6 => 6,
            _ => size
        };
        string? name = args.Has(5) ? BuiltinText.Convert(args.Get(5)) : null;
        string? family = string.IsNullOrEmpty(name) ? null : name.Trim();
        if (family is not null && (family.Length == 0
            || family.Any(character => character < ' ')))
        {
            throw new InvalidOperationException("吹出しのフォント名が不正です。");
        }

        int? textColor = Color(6);
        int? backgroundColor = Color(7);
        int? transparency = Integer(8);
        if (transparency is < -2 or > 255)
        {
            throw new InvalidOperationException("吹出しの透明化は-2～255で指定してください。");
        }

        return new(message, x, y, direction, size, family,
            textColor, backgroundColor, transparency);

        int? Integer(int index) => args.Has(index)
            ? NumericOperations.Integer(args.Get(index)) : null;

        int? Color(int index)
        {
            int? color = Integer(index);
            if (color is < 0 or > 0xFFFFFF)
            {
                throw new InvalidOperationException("吹出しの色は24bitのBGRで指定してください。");
            }

            return color;
        }
    }
}
