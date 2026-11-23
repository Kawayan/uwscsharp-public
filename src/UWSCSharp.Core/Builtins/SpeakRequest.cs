namespace UWSCSharp.Core;

/// <summary>
/// 発声要求と、引数なしの保留消去を区別します。
/// </summary>
internal readonly record struct SpeakRequest(
    string? Text, bool Parallel, bool Interrupt)
{
    internal static void CheckCall(CallExpr call)
    {
        if (call.Arguments.Count > 3
            || call.Arguments.FirstOrDefault() is OmittedExpr)
        {
            throw new InvalidOperationException(
                $"引数の数または省略位置が不正です: {BuiltinFunctionNames.Speak}");
        }
    }

    internal static SpeakRequest Read(IReadOnlyList<Value> arguments)
    {
        if (arguments.Count == 0)
        {
            return new(null, false, false);
        }

        BuiltinFunctions.CheckArguments(BuiltinFunctionNames.Speak, arguments, 1, 3);
        var args = new CallArguments(arguments);
        string text = BuiltinText.Convert(args.Get(0));
        bool parallel = Math.Truncate(NumericOperations.RuntimeNumber(
            args.Get(1, Value.Numeric(0)))) != 0;
        bool interrupt = NumericOperations.RuntimeNumber(
            args.Get(2, Value.Numeric(0))) != 0;
        return new(text, parallel, interrupt);
    }
}
