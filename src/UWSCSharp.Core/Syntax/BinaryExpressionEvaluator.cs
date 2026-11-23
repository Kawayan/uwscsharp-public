namespace UWSCSharp.Core;

/// <summary>
/// 二項演算の左右の順序を保ち、明示したスタックで評価します。
/// </summary>
internal static class BinaryExpressionEvaluator
{
    internal static async ValueTask<Value> EvaluateAsync(Expr expression,
        IEvaluationContext context, CancellationToken token, bool shortCircuit,
        Func<Expr, CancellationToken, ValueTask<Value>>? evaluateOperand = null,
        Func<CancellationToken>? currentToken = null)
    {
        var pending = new Stack<(BinaryExpr Node, Value Left, bool HasLeft)>();
        Expr? current = expression;
        Value result = default;
        while (true)
        {
            CancellationToken activeToken = currentToken?.Invoke() ?? token;
            activeToken.ThrowIfCancellationRequested();
            if (current is BinaryExpr binary)
            {
                pending.Push((binary, default, false));
                current = binary.Left;
                continue;
            }

            if (current is not null)
            {
                // 括弧や関数の内側は、それぞれの通常評価へ委ねます。
                result = await (evaluateOperand is null
                    ? current.EvaluateAsync(context, activeToken)
                    : evaluateOperand(current, activeToken))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                current = null;
            }

            if (!pending.TryPop(out (BinaryExpr Node, Value Left, bool HasLeft) frame))
            {
                return result;
            }

            if (frame.HasLeft)
            {
                result = Expression.Apply(frame.Node.Operator, frame.Left,
                    result, context.SameString, context.NumericArithmetic);
                continue;
            }

            if (shortCircuit && frame.Node.Operator is Keywords.And or Keywords.Or)
            {
                long value = Expression.Integer(result);
                if (frame.Node.Operator == Keywords.And && value == 0)
                {
                    result = Value.Numeric(0);
                    continue;
                }

                if (frame.Node.Operator == Keywords.Or && value != 0)
                {
                    result = Value.Numeric(value);
                    continue;
                }
            }

            pending.Push((frame.Node, result, true));
            current = frame.Node.Right;
        }
    }
}
