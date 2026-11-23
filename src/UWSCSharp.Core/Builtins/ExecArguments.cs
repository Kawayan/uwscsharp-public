namespace UWSCSharp.Core;

/// <summary>
/// 起動前にEXECの全引数を検証します。
/// </summary>
internal sealed record ExecArguments(string Command, bool Wait,
    int? X, int? Y, int? Width, int? Height)
{
    internal bool HasGeometry => X.HasValue || Y.HasValue
        || Width.HasValue || Height.HasValue;

    internal static ExecArguments Read(IReadOnlyList<Value> values)
    {
        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.Exec,
            values, 1, 6);
        var args = new CallArguments(values);
        Value command = args.Get(0);
        if (command.Kind != ValueKind.String
            || string.IsNullOrWhiteSpace(command.Text)
            || command.Text.Contains('\0') || command.Text.Length > 32766)
        {
            throw new InvalidOperationException(
                "EXECにはNULを含まない32766文字以内のコマンド文字列を指定してください。");
        }

        int? Coordinate(int index) => args.Has(index)
            ? NumericOperations.Integer(args.Get(index)) : null;
        int? width = Coordinate(4);
        int? height = Coordinate(5);
        if (width < 0 || height < 0)
        {
            throw new InvalidOperationException(
                "EXECの幅と高さには0以上を指定してください。");
        }

        return new(command.Text, args.Int(1) != 0,
            Coordinate(2), Coordinate(3), width, height);
    }
}
