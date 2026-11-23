namespace UWSCSharp.Core;

/// <summary>
/// 条件文だけで短絡評価し、括弧と呼出しの内側は通常評価します。
/// </summary>
internal sealed record ConditionExpr(Expr Inner) : Expr
{
    public override ValueTask<Value> EvaluateAsync(IEvaluationContext context,
        CancellationToken token) =>
        BinaryExpressionEvaluator.EvaluateAsync(Inner, context, token,
            shortCircuit: true);
}
