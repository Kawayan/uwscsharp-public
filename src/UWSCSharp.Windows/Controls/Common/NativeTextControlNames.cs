namespace UWSCSharp.Windows;

/// <summary>
/// 標準の文字列コントロールと既知のクラス別名を判定します。
/// </summary>
internal static class NativeTextControlNames
{
    internal static bool Matches(string className, string standard) =>
        ControlClassNames.Matches(className, standard)
        || standard == ControlClassNames.Edit
            && (className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("WindowsForms10.RichEdit",
                    StringComparison.OrdinalIgnoreCase));
}
