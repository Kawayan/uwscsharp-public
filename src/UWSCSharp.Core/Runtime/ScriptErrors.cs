namespace UWSCSharp.Core;

/// <summary>
/// 行番号を付けてスクリプトへ通知する例外を判定します。
/// </summary>
internal static class ScriptErrors
{
    internal static bool IsScriptError(Exception error) =>
        error is InvalidOperationException
            or FormatException or OverflowException;
}
