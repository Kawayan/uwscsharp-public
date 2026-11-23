namespace UWSCSharp.Core;

/// <summary>
/// 停止画面の表示と、座標の省略を区別した要求です。
/// </summary>
internal sealed record StopFormRequest(bool Visible, int? X, int? Y)
{
    internal (int X, int Y) ResolvePosition(int currentX, int currentY) =>
        (X ?? currentX, Y ?? currentY);

    internal static StopFormRequest Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count is < 1 or > 3
            || arguments[0].Kind == ValueKind.Omitted)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.StopForm}には表示フラグと省略可能な2座標を指定してください。");
        }

        var args = new CallArguments(arguments);
        bool visible = Math.Truncate(
            NumericOperations.RuntimeNumber(args.Get(0))) != 0;
        int? x = args.Has(1) ? NumericOperations.Integer(args.Get(1)) : null;
        int? y = args.Has(2) ? NumericOperations.Integer(args.Get(2)) : null;
        return new(visible, x, y);
    }
}
