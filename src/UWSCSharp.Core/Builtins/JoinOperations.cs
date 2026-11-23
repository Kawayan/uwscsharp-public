namespace UWSCSharp.Core;

/// <summary>
/// JOINの有限変換と範囲指定を共有します。
/// </summary>
internal static class JoinOperations
{
    internal static string Join(IReadOnlyList<Value> arguments)
    {
        var args = new CallArguments(arguments);
        return ArrayOperations.Join(
            args.Get(0).AsArray(),
            BuiltinText.Convert(args.Get(1, Value.String(" "))),
            Math.Truncate(NumericOperations.RuntimeNumber(args.Get(2))) != 0,
            ArrayOperations.RangeIndex(args.Get(3)),
            ArrayOperations.RangeIndex(args.Get(4, Value.Numeric(int.MaxValue))));
    }
}
