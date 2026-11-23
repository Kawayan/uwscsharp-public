namespace UWSCSharp.Core;

internal enum StatefulBuiltin
{
    Unknown,
    Sleep,
    GetTime,
    Random,
    Token,
    Resize,
    SetClear,
    ShiftArray,
    QSort,
    LogPrint,
    StopForm,
    Eval,
    CtrlTrd,
    SetHotkey,
    Set8087Cw,
    OleEvent,
    RecoState,
    Dictate,
    Speak,
}

/// <summary>
/// 状態を持つ組み込み関数の登録名と実行種別を管理します。
/// </summary>
internal static class StatefulBuiltins
{
    private static readonly Dictionary<string, StatefulBuiltin> Kinds = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [BuiltinFunctionNames.Sleep] = StatefulBuiltin.Sleep,
        [BuiltinFunctionNames.GetTime] = StatefulBuiltin.GetTime,
        [BuiltinFunctionNames.Random] = StatefulBuiltin.Random,
        [BuiltinFunctionNames.Token] = StatefulBuiltin.Token,
        [BuiltinFunctionNames.Resize] = StatefulBuiltin.Resize,
        [BuiltinFunctionNames.SetClear] = StatefulBuiltin.SetClear,
        [BuiltinFunctionNames.ShiftArray] = StatefulBuiltin.ShiftArray,
        [BuiltinFunctionNames.QSort] = StatefulBuiltin.QSort,
        [BuiltinFunctionNames.LogPrint] = StatefulBuiltin.LogPrint,
        [BuiltinFunctionNames.StopForm] = StatefulBuiltin.StopForm,
        [BuiltinFunctionNames.Eval] = StatefulBuiltin.Eval,
        [BuiltinFunctionNames.CtrlTrd] = StatefulBuiltin.CtrlTrd,
        [BuiltinFunctionNames.SetHotkey] = StatefulBuiltin.SetHotkey,
        [BuiltinFunctionNames.Set8087Cw] = StatefulBuiltin.Set8087Cw,
        [BuiltinFunctionNames.OleEvent] = StatefulBuiltin.OleEvent,
        [BuiltinFunctionNames.RecoState] = StatefulBuiltin.RecoState,
        [BuiltinFunctionNames.Dictate] = StatefulBuiltin.Dictate,
        [BuiltinFunctionNames.Speak] = StatefulBuiltin.Speak,
    };

    internal static bool IsBuiltin(string name) => Kinds.ContainsKey(name);

    internal static StatefulBuiltin GetKind(string name) =>
        Kinds.GetValueOrDefault(name);
}
