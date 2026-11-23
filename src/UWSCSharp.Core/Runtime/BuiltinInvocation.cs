namespace UWSCSharp.Core;

internal static class BuiltinInvocation
{
    internal static async ValueTask<Value> EvaluateValidatedAsync(
        CallExpr call, IEvaluationContext context,
        CancellationToken token)
    {
        List<Value> arguments = await EvaluateValidatedArgumentsAsync(
            call, context, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        return BuiltinFunctions.Evaluate(call.Name, arguments,
            context.SameString, context.NumericArithmetic as INumericBuiltinArithmetic);
    }

    internal static async ValueTask<List<Value>> EvaluateValidatedArgumentsAsync(
        CallExpr call, IEvaluationContext context,
        CancellationToken token,
        Func<Expr, CancellationToken, ValueTask<Value>>? evaluate = null)
    {
        token.ThrowIfCancellationRequested();
        // 不正な呼出しでは、引数内の副作用を発生させません。
        BuiltinFunctions.Signature signature = BuiltinFunctions.GetSignature(call.Name);
        int minimum = signature.Min;
        int maximum = signature.Max;
        if (call.Arguments.Count < minimum || call.Arguments.Count > maximum
            || call.Arguments.Take(minimum).Any(argument => argument is OmittedExpr))
        {
            throw new InvalidOperationException(
                $"引数の数または省略位置が不正です: {call.Name.ToUpperInvariant()}");
        }

        return await EvaluateArgumentsAsync(
            call.Arguments, context, token, evaluate).ConfigureAwait(WorkerThreadContext.IsCurrent);
    }

    internal static async ValueTask<List<Value>> EvaluateArgumentsAsync(
        IReadOnlyList<Expr> expressions, IEvaluationContext context,
        CancellationToken token,
        Func<Expr, CancellationToken, ValueTask<Value>>? evaluate = null)
    {
        List<Value> arguments = [];
        foreach (Expr expression in expressions)
        {
            arguments.Add(await (evaluate is null
                ? expression.EvaluateAsync(context, token) : evaluate(expression, token))
                .ConfigureAwait(WorkerThreadContext.IsCurrent));
        }

        return arguments;
    }
}
