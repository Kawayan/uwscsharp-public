using System.Collections.Concurrent;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : ISpeechEngineSession, IScriptSpeechSession
    {
        public Task SendSpeechAsync(SpeechEngine engine, SpeechDispatchRequest request,
            bool discardPending = false, CancellationToken token = default)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                runToken.ThrowIfCancellationRequested();
                return resources.SendSpeechAsync(engine, request, discardPending);
            }
        }

        public Task SubmitSpeechAsync(bool onlySapi, SpeakRequest request,
            CancellationToken token)
        {
            SpeechEngine engine = onlySapi
                ? SpeechEngine.Sapi5 : SpeechEngine.PreferSpeechPlatform;
            if (!request.Parallel || request.Text is null)
            {
                return SubmitSpeechCoreAsync(engine, request, token, keepSelectedEngine: true);
            }

            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                runToken.ThrowIfCancellationRequested();
                _ = resources.SendSpeechAsync(engine, CreateDispatch(request.Text,
                    request.Interrupt), request.Interrupt, reportFailure: true,
                    keepSelectedEngine: true);
            }
            return Task.CompletedTask;
        }

        public Task SubmitSpeechAsync(
            SpeechEngine engine, SpeakRequest request, CancellationToken token) =>
            SubmitSpeechCoreAsync(engine, request, token, keepSelectedEngine: false);

        private async Task SubmitSpeechCoreAsync(SpeechEngine engine,
            SpeakRequest request, CancellationToken token, bool keepSelectedEngine)
        {
            if (request.Text is null)
            {
                lock (disposalGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    token.ThrowIfCancellationRequested();
                    runToken.ThrowIfCancellationRequested();
                    resources.DiscardPendingSpeech();
                }
                return;
            }

            Task sending;
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                runToken.ThrowIfCancellationRequested();
                sending = resources.SendSpeechAsync(engine,
                    CreateDispatch(request.Text, request.Interrupt),
                    request.Parallel && request.Interrupt,
                    keepSelectedEngine: keepSelectedEngine,
                    discardable: request.Parallel);
            }

            await sending.ConfigureAwait(false);
            if (!request.Parallel)
            {
                await WaitForSpeechAsync(token).ConfigureAwait(false);
            }
        }

        private static SpeechDispatchRequest CreateDispatch(
            string text, bool interrupt)
        {
            const int asyncFlag = 1;
            const int purgeFlag = 2;
            return new(text, interrupt ? asyncFlag | purgeFlag : asyncFlag);
        }

        public Task WaitForSpeechAsync(CancellationToken token = default)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                runToken.ThrowIfCancellationRequested();
                return WaitForSpeechCoreAsync(token);
            }
        }

        private async Task WaitForSpeechCoreAsync(CancellationToken token)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, runToken);
            await resources.WaitForSpeechAsync(cancellation.Token).ConfigureAwait(false);
        }
    }

    private sealed partial class RunResources
    {
        private readonly HashSet<CancellationToken> speechRootTokens = [];
        private readonly List<CancellationTokenRegistration> speechRegistrations = [];
        private readonly ConcurrentQueue<Exception> speechFailures = new();
        private SpeechEngineOwner? speech;
        private SpeechEngine? speechEngine;
        private Task? speechStopping;

        internal void ConfigureSpeechCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (token.CanBeCanceled && speechRootTokens.Add(token))
                {
                    speechRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestSpeechStop(), this));
                }
            }
        }

        internal Task SendSpeechAsync(SpeechEngine engine,
            SpeechDispatchRequest request, bool discardPending,
            bool reportFailure = false, bool keepSelectedEngine = false,
            bool discardable = true)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Text);
            lock (gate)
            {
                CheckSpeechAvailable();
                if (!keepSelectedEngine && speechEngine is not null && speechEngine != engine)
                {
                    throw new InvalidOperationException("同じ実行の音声エンジンは変更できません。");
                }

                speech ??= new SpeechEngineOwner(engine, comRuntime);
                speechEngine ??= engine;
                return speech.Enqueue(request, discardPending,
                    reportFailure ? speechFailures.Enqueue : null, discardable);
            }
        }

        internal Exception? TakeSpeechFailure() =>
            speechFailures.TryDequeue(out Exception? error) ? error : null;

        internal void DiscardPendingSpeech()
        {
            lock (gate)
            {
                CheckSpeechAvailable();
                speech?.DiscardPending();
            }
        }

        internal Task WaitForSpeechAsync(CancellationToken token)
        {
            lock (gate)
            {
                CheckSpeechAvailable();
                return speech?.WaitUntilDoneAsync(token) ?? Task.CompletedTask;
            }
        }

        private void CheckSpeechAvailable()
        {
            ObjectDisposedException.ThrowIf(closing, this);
            foreach (CancellationToken token in speechRootTokens)
            {
                token.ThrowIfCancellationRequested();
            }
        }

        private void RequestSpeechStop()
        {
            lock (gate)
            {
                speechStopping ??= speech?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            }
        }

        private async Task DisposeSpeechAsync(List<Exception> errors)
        {
            Task stopping;
            CancellationTokenRegistration[] registrations;
            lock (gate)
            {
                stopping = speechStopping ??= speech?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                registrations = [.. speechRegistrations];
            }

            try
            {
                // 発声待ちのworkerが終了できるよう、forkの終了待ちより先に停止します。
                await stopping.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(stopping.Exception ?? failure);
            }

            while (TakeSpeechFailure() is { } failure)
            {
                errors.Add(failure);
            }

            foreach (CancellationTokenRegistration registration in registrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
