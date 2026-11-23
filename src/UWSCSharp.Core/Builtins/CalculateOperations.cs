namespace UWSCSharp.Core;

/// <summary>
/// CALCARRAYの有限モードと範囲指定を共有します。
/// </summary>
internal static class CalculateOperations
{
    internal static Value Calculate(IReadOnlyList<Value> arguments)
    {
        var args = new CallArguments(arguments);
        UwscArray array = args.Get(0).AsArray();
        double mode = Math.Truncate(NumericOperations.RuntimeNumber(args.Get(1)));
        if (mode is < 1 or > 4)
        {
            throw new InvalidOperationException("CALCARRAYの計算法が不正です。");
        }

        return ArrayOperations.Calculate(array, (int)mode,
            ArrayOperations.RangeIndex(args.Get(2)),
            ArrayOperations.RangeIndex(args.Get(3, Value.Numeric(int.MaxValue))));
    }
}
