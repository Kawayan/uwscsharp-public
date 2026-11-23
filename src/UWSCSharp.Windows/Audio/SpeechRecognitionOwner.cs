using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識器と結果欄を所有し、通知失敗と終了を待機中の取得へ伝えます。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SpeechRecognitionOwner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SpeechRecognitionControlQueue operations = new();
    private readonly SpeechRecognitionResults results = new();
    private readonly CancellationTokenSource ending = new();
    private readonly Func<Action<string>, CancellationToken,
        Task<ISpeechRecognitionSession>> create;
    private Task<ISpeechRecognitionSession>? initialization;
    private ISpeechRecognitionSession? session;
    private Task? monitoring;
    private TaskCompletionSource? disposal;
    private Exception? notificationFailure;
    private Exception? initializationFailure;

    internal SpeechRecognitionOwner(IComRuntime? runtime = null)
        : this(async (receive, token) =>
            await ComSpeechRecognition.CreateAsync(receive, runtime, token)
                .ConfigureAwait(false))
    {
    }

    internal SpeechRecognitionOwner(Func<Action<string>, CancellationToken,
        Task<ISpeechRecognitionSession>> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        this.create = create;
    }

    internal Task StartDictationAsync(CancellationToken token = default) =>
        ControlAsync(true, (current, cancellation) => current.StartDictationAsync(
            cancellation, ClearResults), token);

    internal Task StartGrammarFileAsync(string path,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ControlAsync(true, (current, cancellation) =>
            current.StartGrammarFileAsync(path, cancellation, ClearResults), token);
    }

    internal Task StartWordsAsync(IEnumerable<string> words,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        return ControlAsync(true, (current, cancellation) =>
            current.StartWordsAsync(words, cancellation, ClearResults), token);
    }

    internal Task StartEvaluatedWordsAsync(
        Func<CancellationToken, Task<IEnumerable<string>>> evaluate,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        return ControlAsync(true, (current, cancellation) =>
            current.StartEvaluatedWordsAsync(evaluate, cancellation, ClearResults), token);
    }

    internal Task StopAsync(CancellationToken token = default) =>
        ControlAsync(false, (current, cancellation) => current.StopAsync(cancellation), token);

    internal string TakeLatest() => results.TakeLatest();

    internal string? GetScriptErrorMessage(Exception failure) =>
        session?.GetScriptErrorMessage(failure);

    internal Task<string> WaitForNextAsync(CancellationToken token = default) =>
        results.WaitForNextAsync(token);

    private void ClearResults() => results.TakeLatest();

    private async Task ControlAsync(bool start,
        Func<ISpeechRecognitionSession, CancellationToken, Task> action,
        CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
        }

        await operations.RunAsync(async () =>
        {
            CancellationToken lifetime;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposal is not null, this);
                lifetime = ending.Token;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            if (initialization is null && start)
            {
                // 作成中の終了を所有者が待てるよう、要求側の取消と寿命を分けます。
                initialization = CreateSessionAsync(lifetime);
            }

            if (initialization is null)
            {
                return;
            }

            ISpeechRecognitionSession current = await initialization
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            cancellation.Token.ThrowIfCancellationRequested();
            await action(current, cancellation.Token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
        }, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
    }

    private async Task<ISpeechRecognitionSession> CreateSessionAsync(CancellationToken token)
    {
        try
        {
            ISpeechRecognitionSession current = await create(Receive, token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(current);
            session = current;
            monitoring = MonitorAsync(current, token);
            return current;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            lock (gate)
            {
                // 呼出し元へ渡した初期化失敗を、終了時に重ねて送出しません。
                initializationFailure = failure;
                results.Fail(failure);
            }
            throw;
        }
    }

    private void Receive(string text)
    {
        lock (gate)
        {
            // 終了した所有者へ届いた通知は、次の実行の結果欄へ渡しません。
            if (disposal is null && notificationFailure is null
                && initializationFailure is null)
            {
                results.Publish(text);
            }
        }
    }

    private void ReportFailure(Exception failure)
    {
        lock (gate)
        {
            notificationFailure ??= failure;
            results.Fail(failure);
        }
    }

    private async Task MonitorAsync(ISpeechRecognitionSession current, CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (current.TakeCallbackFailure() is Exception failure)
                {
                    ReportFailure(failure);
                    return;
                }

                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            ReportFailure(failure);
        }
    }

    /// <summary>
    /// 取得の待機を解除し、認識処理の終了と参照回収を待ちます。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposal is not null)
            {
                return new(disposal.Task);
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion;
        }

        results.Dispose();
        _ = FinishDisposalAsync(completion);
        return new(completion.Task);
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try
        {
            await ending.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (monitoring is not null)
            {
                await monitoring.ConfigureAwait(false);
            }

            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }

                try
                {
                    if (session.TakeCallbackFailure() is Exception lateFailure)
                    {
                        failures.Add(lateFailure);
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            if (notificationFailure is not null)
            {
                failures.Add(notificationFailure);
            }
        }
        finally
        {
            operations.Release();
            ending.Dispose();
        }

        if (failures.Count == 0)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(new AggregateException(failures));
        }
    }
}
