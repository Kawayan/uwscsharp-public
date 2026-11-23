namespace UWSCSharp.Core;

/// <summary>
/// CTRLWINの命令と32bitメッセージ引数を保持します。
/// </summary>
internal readonly record struct WindowControlArguments(
    int Id, uint Command, uint WParam, int LParam)
{
    internal static WindowControlArguments Read(
        IReadOnlyList<Value> values)
    {
        if (values[0].Kind == ValueKind.Empty
            || values[1].Kind == ValueKind.Empty)
        {
            throw new InvalidOperationException(
                "CTRLWINにはウィンドウIDと命令を指定してください。");
        }

        var args = new CallArguments(values);
        return new WindowControlArguments(args.Int(0),
            Unsigned(args.Get(1)), Unsigned(args.Get(2)),
            args.Int(3));
    }

    private static uint Unsigned(Value value)
    {
        double number = Math.Truncate(NumericOperations.Number(value));
        if (number < uint.MinValue || number > uint.MaxValue)
        {
            throw new InvalidOperationException(
                "CTRLWINの命令またはwParamが32bit符号なし整数の範囲外です。");
        }

        return (uint)number;
    }
}
