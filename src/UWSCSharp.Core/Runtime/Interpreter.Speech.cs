namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private volatile bool onlySapi;

        private async ValueTask<Value> InvokeSpeakAsync(CallExpr call, Scope caller)
        {
            SpeakRequest.CheckCall(call);
            IScriptSpeechSession speech = windows as IScriptSpeechSession
                ?? throw new InvalidOperationException(
                    "この実行先は音声合成に対応していません。");
            List<Value> values = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            var request = SpeakRequest.Read(values);
            try
            {
                await WaitWithAutomationFailureAsync(() => new ValueTask(
                    speech.SubmitSpeechAsync(onlySapi, request, token)))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception failure) when (failure is System.Runtime.InteropServices.COMException
                or InvalidOperationException or IOException or AggregateException)
            {
                throw new InvalidOperationException(
                    $"{BuiltinFunctionNames.Speak}: {failure.Message}", failure);
            }

            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.Empty;
        }
    }
}
