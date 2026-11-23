namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Value> InvokeSpeechRecognitionAsync(
            CallExpr call, Scope caller, StatefulBuiltin kind)
        {
            SpeechRecognitionFlag.CheckCall(call.Name, call.Arguments);
            IScriptSpeechRecognitionSession recognition = windows as IScriptSpeechRecognitionSession
                ?? throw new InvalidOperationException("この実行先は音声認識に対応していません。");
            bool flag = kind == StatefulBuiltin.Dictate;
            int flagCount = kind == StatefulBuiltin.Dictate
                ? call.Arguments.Count : Math.Min(1, call.Arguments.Count);
            for (int index = 0; index < flagCount; index++)
            {
                Value value = await call.Arguments[index].EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                bool current = await RecognitionOperationAsync(call.Name,
                    () => recognition.ReadRecognitionFlagAsync(value, call.Name, token))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (index == 0)
                {
                    flag = current;
                }
            }

            Exception? evaluationFailure = null;
            Value result = await RecognitionOperationAsync(call.Name, async () =>
            {
                if (kind == StatefulBuiltin.Dictate)
                {
                    return Value.String(await recognition.ReadRecognitionAsync(flag, token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent));
                }

                if (!flag)
                {
                    await recognition.StopRecognitionAsync(token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                else
                {
                    await recognition.StartRecognitionAsync(call.Arguments.Count > 1 ? EvaluateWords : null, token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                return Value.String("");
            }, failure => ReferenceEquals(failure, evaluationFailure))
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return result;

            async Task<IEnumerable<string>> EvaluateWords(CancellationToken cancellation)
            {
                try
                {
                    return await SpeechRecognitionWordEvaluation.EvaluateAsync(
                        [.. call.Arguments.Skip(1)], caller,
                        (value, active) => RecognitionOperationAsync(call.Name,
                            () => recognition.FormatRecognitionWordAsync(value, active)),
                        call.PreserveSpecialCharacters, cancellation)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (NotSupportedException failure)
                {
                    var scriptError = new InvalidOperationException(failure.Message, failure);
                    evaluationFailure = scriptError;
                    throw scriptError;
                }
                catch (Exception failure)
                {
                    // 語彙式の失敗を、外側の認識制御でhost障害へ包み直しません。
                    evaluationFailure = failure;
                    throw;
                }
            }
        }

        private async ValueTask<T> RecognitionOperationAsync<T>(string function,
            Func<ValueTask<T>> action, Func<Exception, bool>? scriptFailure = null)
        {
            try
            {
                return await WaitWithAutomationFailureAsync(action)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                and not StopSignal and not AutomationFailureException and not HostFailure
                && scriptFailure?.Invoke(failure) != true && !IsComCallbackScriptError(failure))
            {
                string? message;
                try
                {
                    message = windows!.GetScriptErrorMessage(function, failure);
                }
                catch (Exception mappingFailure) when (mappingFailure is not OperationCanceledException)
                {
                    throw new HostFailure(mappingFailure);
                }

                if (message is not null)
                {
                    throw new InvalidOperationException(message, failure);
                }

                throw new HostFailure(failure);
            }
        }
    }
}
