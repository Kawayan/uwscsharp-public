namespace UWSCSharp.Core;

/// <summary>
/// スクリプトのx87制御語をCOMメンバー呼出しに渡します。
/// </summary>
internal interface IControlledComMemberInvoker
{
    ValueTask<Value> InvokeControlledMemberAsync(Value target, string name,
        IReadOnlyList<Value> arguments, Value? assigned, ushort controlWord,
        CancellationToken token);

    ValueTask<WindowsMemberResult> InvokeControlledMemberWithReferencesAsync(
        Value target, string name, IReadOnlyList<WindowsMemberArgument> arguments,
        Value? assigned, ushort controlWord, CancellationToken token);
}
