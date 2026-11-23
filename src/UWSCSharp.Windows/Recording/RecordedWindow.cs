using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

// 操作時点の対象情報を保存し、終了後に消えたウインドウも記録へ残します。
internal sealed record RecordedWindow(
    nint Handle, string Title, string ClassName,
    int X, int Y, int Width, int Height)
{
    internal static RecordedWindow? Capture(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        window = Native.GetAncestor(window, 2);
        if (window == 0 || !Native.GetWindowRect(window, out Native.Rect rect))
        {
            return null;
        }

        int titleCapacity = Math.Clamp(
            Native.GetWindowTextLengthW(window) + 1, 1, 32_768);
        var title = new StringBuilder(titleCapacity);
        var className = new StringBuilder(256);
        Native.GetWindowTextW(window, title, title.Capacity);
        if (Native.GetClassNameW(window, className, className.Capacity) == 0)
        {
            return null;
        }

        return new(window, title.ToString(), className.ToString(),
            rect.Left, rect.Top, rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    internal string Identifier() =>
        $"{BuiltinFunctionNames.GetId}({Literal(Title)},{Literal(ClassName)})";

    internal string Command() =>
        $"{BuiltinFunctionNames.Acw}({Identifier()},{X},{Y},{Width},{Height})";

    internal string MovementCommand() =>
        $"{BuiltinFunctionNames.Acw}({Identifier()},{X},{Y},{Width},{Height},0)";

    internal static string Literal(string value)
    {
        var text = new StringBuilder("\"");
        foreach (char character in value)
        {
            // 引用符・改行・特殊文字マーカーをスクリプトの構文と分離します。
            if (character is '"' or '<' or '\r' or '\n')
            {
                text.Append($"\"+{BuiltinFunctionNames.Chr}({(int)character})+\"");
            }
            else
            {
                text.Append(character);
            }
        }

        return text.Append('"').ToString();
    }
}
