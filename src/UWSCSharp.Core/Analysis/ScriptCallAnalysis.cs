namespace UWSCSharp.Core;

/// <summary>
/// 実行を伴わない呼出し診断1件です。
/// </summary>
/// <param name="Line">呼出しを含む文または宣言の開始行です。</param>
/// <param name="Code">診断の種類を表す識別子です。</param>
/// <param name="Function">呼出しまたは宣言に記述された関数名です。</param>
/// <param name="Message">静的に確認できた問題の説明です。</param>
public sealed record ScriptCallDiagnostic(
    int Line, string Code, string Function, string Message);

/// <summary>
/// 呼出しの警告と、静的には確認していない対象の集計です。
/// </summary>
/// <param name="Diagnostics">最大100件の警告です。</param>
/// <param name="Truncated">上限を超える警告を省略したかどうかです。</param>
/// <param name="UncheckedMemberAccesses">解決していないメンバーアクセス数です。</param>
/// <param name="UncheckedDynamicCalls">内容や指定先を調べていない動的呼出し数です。</param>
/// <param name="UncheckedSyntaxNodes">対応する走査規則がない構文の数です。</param>
public sealed record ScriptCallAnalysis(
    IReadOnlyList<ScriptCallDiagnostic> Diagnostics,
    bool Truncated,
    int UncheckedMemberAccesses,
    int UncheckedDynamicCalls,
    int UncheckedSyntaxNodes);
