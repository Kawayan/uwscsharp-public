namespace UWSCSharp.Core;

/// <summary>
/// 解析量と実行の再帰を制限する上限値です。
/// </summary>
internal static class ScriptLimits
{
    internal const int MaxExternalScripts = 128;
    internal const int MaxCallDepth = 128;
    internal const int MaxStatementNesting = 128;
    internal const int MaxExpressionNesting = 128;
    internal const int MaxExpressionTokens = 4096;
}
