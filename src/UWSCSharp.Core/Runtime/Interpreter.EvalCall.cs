namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Value> InvokeEvalCallAsync(string text,
            CallExpr evalCall, Scope caller)
        {
            string targetText = text[Keywords.Call.Length..].Trim();
            if (targetText.StartsWith('"') && !targetText.EndsWith(')'))
            {
                Value dynamicTarget = await new Expression(targetText,
                    evalCall.PreserveSpecialCharacters, cancellationToken: token)
                    .ParseExpression().EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                ThrowIfStoppedNativeDllRoutine();
                if (dynamicTarget.Kind != ValueKind.String)
                {
                    throw new InvalidOperationException(
                        "CALLの呼出し先には文字列を指定してください。");
                }
                // 引用した式の結果は、file名と引数を含む呼出し文字列です。
                text = Keywords.Call + " " + dynamicTarget.Text;
            }
            return await InvokeDynamicCallAsync(text, evalCall, caller)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
    }
}
