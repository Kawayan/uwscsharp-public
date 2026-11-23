using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 送信キューと音声エンジンを所有し、停止要求後に参照を解放します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SpeechEngineOwner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SpeechDispatchQueue queue;
    private readonly CancellationTokenSource waiting = new();
    private Task<ComSpeechVoice>? initialization;
    private ComSpeechVoice? voice;
    private TaskCompletionSource? disposal;

    internal SpeechEngineOwner(SpeechEngine engine, IComRuntime? runtime = null)
    {
        if (!Enum.IsDefined(engine))
        {
            throw new ArgumentOutOfRangeException(nameof(engine));
        }

        queue = new(async (request, token) =>
        {
            // 作成失敗も保持し、後続要求で暗黙に再試行しません。
            initialization ??= ComSpeechVoice.CreateAsync(engine, runtime, token);
            voice = await initialization.ConfigureAwait(false);
            await voice.SpeakAsync(request, token).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 要求の送信完了を返します。発声完了は表しません。
    /// </summary>
    internal Task Enqueue(SpeechDispatchRequest request, bool discardPending = false,
        Action<Exception>? reportFailure = null, bool discardable = true)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            return queue.Enqueue(request, discardPending, reportFailure, discardable);
        }
    }

    internal void DiscardPending()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            queue.DiscardPending();
        }
    }

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

        // COM処理と取消callbackを所有者のロック外で終了させます。
        _ = FinishDisposalAsync(completion);
        return new(completion.Task);
    }

    /// <summary>
    /// 先行要求の送信後、エンジン全体の発声完了を待ちます。
    /// </summary>
    internal Task WaitUntilDoneAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            token.ThrowIfCancellationRequested();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                token, waiting.Token);
            return WaitCoreAsync(queue.DrainAsync(), cancellation);
        }
    }

    private async Task WaitCoreAsync(Task sent,
        CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            CancellationToken token = cancellation.Token;
            await sent.WaitAsync(token).ConfigureAwait(false);
            Task<ComSpeechVoice>? created = initialization;
            if (created is null)
            {
                return;
            }

            ComSpeechVoice current = await created.WaitAsync(token).ConfigureAwait(false);
            while (!await current.WaitUntilDoneAsync(100, token).ConfigureAwait(false))
            {
                // 各COM呼出しを有限時間に分け、間で取消を確認します。
                token.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try
        {
            await waiting.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            await queue.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        // 送信処理が戻った後なので、作成中の参照と競合しません。
        if (voice is not null)
        {
            try
            {
                // 終了済みのキューの取消tokenを停止要求に流用しません。
                await voice.SpeakAsync(new("", 3), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            try
            {
                await voice.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        waiting.Dispose();
        if (failures.Count == 0)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failures);
        }
    }
}
