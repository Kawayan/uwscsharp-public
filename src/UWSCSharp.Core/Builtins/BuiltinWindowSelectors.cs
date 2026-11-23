namespace UWSCSharp.Core;

/// <summary>
/// 特殊ウィンドウ定数の実行時の識別文字列を管理します。
/// </summary>
internal static class BuiltinWindowSelectors
{
    internal const string GetActiveWin = "__" + BuiltinConstantNames.GetActiveWin + "__";
    internal const string GetFrompointWin = "__" + BuiltinConstantNames.GetFrompointWin + "__";
    internal const string GetFrompointObj = "__" + BuiltinConstantNames.GetFrompointObj + "__";
    internal const string GetThisUwscWin = "__" + BuiltinConstantNames.GetThisUwscWin + "__";
    internal const string GetLogPrintWin = "__" + BuiltinConstantNames.GetLogPrintWin + "__";
    internal const string GetFukidasiWin = "__" + BuiltinConstantNames.GetFukidasiWin + "__";
    internal const string GetFormWin = "__" + BuiltinConstantNames.GetFormWin + "__";
    internal const string GetFormWin2 = "__" + BuiltinConstantNames.GetFormWin2 + "__";
    internal const string GetScheduleWin = "__" + BuiltinConstantNames.GetScheduleWin + "__";
    internal const string GetStopFormWin = "__" + BuiltinConstantNames.GetStopFormWin + "__";
    internal const string GetMenuHnd = "__" + BuiltinConstantNames.GetMenuHnd + "__";
    internal const string GetSysMenuHnd = "__" + BuiltinConstantNames.GetSysMenuHnd + "__";

    internal static bool TryGet(string name, out string selector)
    {
        selector = name.ToUpperInvariant() switch
        {
            BuiltinConstantNames.GetActiveWin => GetActiveWin,
            BuiltinConstantNames.GetFrompointWin => GetFrompointWin,
            BuiltinConstantNames.GetFrompointObj => GetFrompointObj,
            BuiltinConstantNames.GetThisUwscWin => GetThisUwscWin,
            BuiltinConstantNames.GetLogPrintWin => GetLogPrintWin,
            BuiltinConstantNames.GetBalloonWin or BuiltinConstantNames.GetFukidasiWin => GetFukidasiWin,
            BuiltinConstantNames.GetFormWin => GetFormWin,
            BuiltinConstantNames.GetFormWin2 => GetFormWin2,
            BuiltinConstantNames.GetScheduleWin => GetScheduleWin,
            BuiltinConstantNames.GetStopFormWin => GetStopFormWin,
            BuiltinConstantNames.GetMenuHnd => GetMenuHnd,
            BuiltinConstantNames.GetSysMenuHnd => GetSysMenuHnd,
            _ => ""
        };
        return selector.Length != 0;
    }
}
