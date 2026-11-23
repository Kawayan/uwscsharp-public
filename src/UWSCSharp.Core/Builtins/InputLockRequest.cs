namespace UWSCSharp.Core;

/// <summary>
/// 入力禁止フラグを検証し、実行時の要求へ変換します。
/// </summary>
internal sealed record InputLockRequest(bool Blocked)
{
    internal static InputLockRequest Read(IReadOnlyList<Value> arguments)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.LockHard, arguments, 0, 1);
        var args = new CallArguments(arguments);
        return new(Math.Truncate(NumericOperations.RuntimeNumber(args.Get(0))) != 0);
    }
}
