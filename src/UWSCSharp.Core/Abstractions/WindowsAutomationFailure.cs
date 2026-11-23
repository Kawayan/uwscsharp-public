namespace UWSCSharp.Core;

/// <summary>
/// 非同期障害を発生元の関数名と元例外を保持して通知します。
/// </summary>
public sealed record WindowsAutomationFailure(string FunctionName, Exception Error);
