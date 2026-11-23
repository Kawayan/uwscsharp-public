namespace UWSCSharp.Core;

/// <summary>
/// 選択的入力禁止の公開引数を、OS操作前に検証します。
/// </summary>
internal sealed record SelectiveInputLockRequest(int? WindowId, int Mode)
{
    internal bool IsRelease => WindowId is null;

    internal static SelectiveInputLockRequest Read(
        IReadOnlyList<Value> arguments)
    {
        BuiltinFunctions.CheckArguments(
            BuiltinFunctionNames.LockHardEx, arguments, 0, 2);
        if (arguments.Count == 0)
        {
            return new(null, 0);
        }

        int id = ReadInteger(arguments[0], int.MaxValue, "ID");
        var args = new CallArguments(arguments);
        int mode = args.Has(1)
            ? ReadInteger(args.Get(1), 2, "モード") : 0;
        return new(id, mode);
    }

    private static int ReadInteger(Value value, int maximum,
        string argument)
    {
        if (value.Kind is not (ValueKind.Number or ValueKind.String))
        {
            throw Error($"{argument}には数値または数値文字列を指定してください。");
        }

        double number;
        try
        {
            number = NumericOperations.RuntimeNumber(value);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.LockHardEx}: {argument}を数値に変換できません。",
                error);
        }

        if (!double.IsFinite(number) || number != Math.Truncate(number)
            || number < 0 || number > maximum)
        {
            throw Error($"{argument}には0～{maximum}の整数を指定してください。");
        }

        return (int)number;
    }

    private static InvalidOperationException Error(string message)
        => new($"{BuiltinFunctionNames.LockHardEx}: {message}");
}
