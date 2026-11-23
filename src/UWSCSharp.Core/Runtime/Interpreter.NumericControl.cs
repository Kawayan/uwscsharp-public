namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Value> InvokeNumericControlAsync(
            CallExpr call, Scope caller)
        {
            if (call.Arguments.Count > 1)
            {
                throw new InvalidOperationException(
                    $"引数の数が不正です: {BuiltinFunctionNames.Set8087Cw}");
            }

            if (State.NumericArithmetic is not INumericControlArithmetic control)
            {
                throw new InvalidOperationException(
                    $"この実行環境では{BuiltinFunctionNames.Set8087Cw}を使用できません。");
            }

            int word = 0;
            if (call.Arguments.Count == 1 && call.Arguments[0] is not OmittedExpr)
            {
                Value value = await call.Arguments[0].EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                word = NumericOperations.Integer(value);
                if (word is < ushort.MinValue or > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"{BuiltinFunctionNames.Set8087Cw}の引数は0から65535の範囲で指定してください。");
                }
            }

            token.ThrowIfCancellationRequested();
            control.SetControlWord((ushort)word);
            return Value.String(string.Empty);
        }
    }
}
