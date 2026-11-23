using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : ISpeechRecognitionRunSession, IScriptSpeechRecognitionSession
    {
        public async ValueTask<bool> ReadRecognitionFlagAsync(Value value, string function,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Value numeric = value;
                if (value.Kind == ValueKind.Number
                    && ((value.IsLogical && value.Number is 0 or 1)
                        || (value.NativeVariantType == 11 && value.GetUnchangedNativeScalar() is bool)))
                {
                    numeric = Value.Numeric(NumericOperations.RuntimeNumber(value));
                }

                string text = numeric.Kind == ValueKind.Nothing && numeric.NativeVariantType is null or 9
                    ? "0" : await new SpeechRecognitionWordFormatter().FormatAsync(numeric, token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                return SpeechRecognitionFlag.Read(text);
            }
            catch (Exception failure) when (failure is InvalidOperationException
                or NotSupportedException or ArgumentException or OverflowException)
            {
                token.ThrowIfCancellationRequested();
                throw ScriptError(function, failure);
            }
        }

        public async ValueTask<string> FormatRecognitionWordAsync(Value value, CancellationToken token)
        {
            try
            {
                return await new SpeechRecognitionWordFormatter().FormatAsync(value, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception failure) when (failure is NotSupportedException
                || ComOperationErrors.Contains(failure))
            {
                throw ScriptError(BuiltinFunctionNames.RecoState, failure);
            }
        }

        public Task StartRecognitionWordsAsync(IReadOnlyList<Expr> expressions,
            IEvaluationContext context, bool preserveSpecialCharacters,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(expressions);
            ArgumentNullException.ThrowIfNull(context);
            var formatter = new SpeechRecognitionWordFormatter();
            // 式の評価は旧文法の停止と結果消去が終わるまで遅らせます。
            return StartRecognitionAsync(cancellation =>
                SpeechRecognitionWordEvaluation.EvaluateAsync(expressions, context,
                    formatter.FormatAsync, preserveSpecialCharacters, cancellation), token);
        }

        public Task StartRecognitionAsync(
            Func<CancellationToken, Task<IEnumerable<string>>>? evaluate = null,
            CancellationToken token = default) =>
            RecognitionOperationAsync(async cancellation =>
            {
                SpeechRecognitionOwner owner = resources.Recognition;
                if (evaluate is null)
                {
                    await owner.StartDictationAsync(cancellation)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                else
                {
                    await owner.StartEvaluatedWordsAsync(evaluate, cancellation)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                return string.Empty;
            }, token);

        public Task StopRecognitionAsync(CancellationToken token = default) =>
            RecognitionOperationAsync(async cancellation =>
            {
                await resources.Recognition.StopAsync(cancellation)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                return string.Empty;
            }, token);

        public Task<string> ReadRecognitionAsync(bool wait,
            CancellationToken token = default) =>
            RecognitionOperationAsync(cancellation =>
            {
                SpeechRecognitionOwner owner = resources.Recognition;
                return wait ? owner.WaitForNextAsync(cancellation)
                    : Task.FromResult(owner.TakeLatest());
            }, token);

        private async Task<string> RecognitionOperationAsync(
            Func<CancellationToken, Task<string>> action,
            CancellationToken token)
        {
            CancellationToken lifetime;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                lifetime = runToken;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            cancellation.Token.ThrowIfCancellationRequested();
            return await action(cancellation.Token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
    }

    private sealed partial class RunResources
    {
        internal string? GetRecognitionScriptErrorMessage(Exception failure) =>
            failure is SpeechRecognitionInitializationException ? failure.Message
                : recognition?.GetScriptErrorMessage(failure);

        private readonly HashSet<CancellationToken> recognitionRootTokens = [];
        private readonly List<CancellationTokenRegistration> recognitionRegistrations = [];
        private SpeechRecognitionOwner? recognition;
        private Task? recognitionStopping;

        internal SpeechRecognitionOwner Recognition
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(closing, this);
                    foreach (CancellationToken token in recognitionRootTokens)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    return recognition ??= new SpeechRecognitionOwner(comRuntime);
                }
            }
        }

        internal void ConfigureRecognitionCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (token.CanBeCanceled && recognitionRootTokens.Add(token))
                {
                    recognitionRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestRecognitionStop(), this));
                }
            }
        }

        private void RequestRecognitionStop()
        {
            lock (gate)
            {
                recognitionStopping ??= recognition?.DisposeAsync().AsTask()
                    ?? Task.CompletedTask;
            }
        }

        private async Task DisposeRecognitionAsync(List<Exception> errors)
        {
            Task stopping;
            CancellationTokenRegistration[] registrations;
            lock (gate)
            {
                stopping = recognitionStopping ??= recognition?.DisposeAsync().AsTask()
                    ?? Task.CompletedTask;
                registrations = [.. recognitionRegistrations];
            }

            try
            {
                // 結果待ちのworkerを解除してから、forkの終了を待ちます。
                await stopping.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(stopping.Exception ?? failure);
            }

            foreach (CancellationTokenRegistration registration in registrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
