namespace UWSCSharp.Core;
/// <summary>
/// ファイルアクセス、待機、出力を行わずに単一の式を評価します。
/// </summary>
public static class ExpressionEvaluator
{
    /// <summary>
    /// 指定した入力を評価し、結果を返します。
    /// </summary>
    public static Value Evaluate(string expression,
        IReadOnlyDictionary<string, Value>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var names = new Dictionary<string, Value>(
            StringComparer.OrdinalIgnoreCase);
        if (variables is not null)
        {
            foreach (KeyValuePair<string, Value> pair in variables)
            {
                names.Add(pair.Key, pair.Value);
            }
        }

        return new Expression(expression).ParseExpression().EvaluateAsync(new PureContext(names), default).GetAwaiter().GetResult();
    }

    private sealed class PureContext(
        IReadOnlyDictionary<string, Value> variables) : IEvaluationContext
    {
        public Value Read(
            string name) => variables.TryGetValue(name,
            out Value value) || BuiltinConstants.TryGet(name,
            out value) ? value : throw new InvalidOperationException(
            $"未定義の変数: {name}");

        /// <summary>
        /// 指定した自動操作関数を非同期に実行します。
        /// </summary>
        public async ValueTask<Value> InvokeAsync(
            CallExpr call, CancellationToken token)
        {
            if (!BuiltinFunctions.IsPure(call.Name))
            {
                throw new InvalidOperationException(
                    "単独の式評価APIでは純粋な組み込み関数だけを呼び出せます。");
            }

            if (BuiltinFunctions.ValidatesBeforeEvaluation(call.Name))
            {
                return await BuiltinInvocation.EvaluateValidatedAsync(
                    call, this, token).ConfigureAwait(false);
            }

            var arguments = new List<Value>();
            foreach (Expr argument in call.Arguments)
            {
                arguments.Add(await argument.EvaluateAsync(
                    this, token).ConfigureAwait(false));
            }

            return BuiltinFunctions.Evaluate(call.Name,
                arguments);
        }
    }
}
