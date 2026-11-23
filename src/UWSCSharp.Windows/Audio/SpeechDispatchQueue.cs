namespace UWSCSharp.Windows;

/// <summary>
/// エンジンへ送る文字列と未変換のフラグです。
/// </summary>
internal sealed record SpeechDispatchRequest(string Text, int Flags);

/// <summary>
/// 発声要求の送信を直列化します。送信完了は発声完了ではありません。
/// </summary>
internal sealed class SpeechDispatchQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Queue<Entry> pending = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<SpeechDispatchRequest, CancellationToken, Task> send;
    private readonly Task dispatcher;
    private TaskCompletionSource? disposal;

    internal SpeechDispatchQueue(
        Func<SpeechDispatchRequest, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        this.send = send;
        dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>
    /// 要求を追加し、その要求の送信結果を返します。
    /// </summary>
    internal Task Enqueue(SpeechDispatchRequest request,
        bool discardPending = false, Action<Exception>? reportFailure = null,
        bool discardable = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        return EnqueueEntry(new(request, reportFailure, discardable), discardPending);
    }

    /// <summary>
    /// ここまでに追加した要求の送信処理が戻るのを待ちます。
    /// </summary>
    internal Task DrainAsync() => EnqueueEntry(new(null, null, Discardable: false), false);

    private Task EnqueueEntry(Entry entry, bool discardPending)
    {
        List<Entry> discarded;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            discarded = discardPending ? TakeDiscardable() : [];
            pending.Enqueue(entry);
            available.Release();
        }

        CancelPending(discarded);
        return entry.Sent.Task;
    }

    /// <summary>
    /// 取り消せる未送信の要求だけを消去します。
    /// </summary>
    internal void DiscardPending()
    {
        List<Entry> discarded;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            discarded = TakeDiscardable();
        }

        CancelPending(discarded);
    }

    public ValueTask DisposeAsync()
    {
        Entry[] discarded;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposal is not null)
            {
                return new(disposal.Task);
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion;
            discarded = TakePending();
        }

        CancelPending(discarded);
        _ = FinishDisposalAsync(completion);
        return new(completion.Task);
    }

    private Entry[] TakePending()
    {
        Entry[] entries = [.. pending];
        pending.Clear();
        return entries;
    }

    private List<Entry> TakeDiscardable()
    {
        var discarded = new List<Entry>();
        int count = pending.Count;
        for (int i = 0; i < count; i++)
        {
            Entry entry = pending.Dequeue();
            if (entry.Discardable)
            {
                discarded.Add(entry);
            }
            else
            {
                pending.Enqueue(entry);
            }
        }
        return discarded;
    }

    private static void CancelPending(IEnumerable<Entry> entries)
    {
        foreach (Entry entry in entries)
        {
            entry.Sent.TrySetCanceled();
        }
    }

    private async Task DispatchAsync()
    {
        while (true)
        {
            try
            {
                await available.WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Entry entry;
            lock (gate)
            {
                if (disposal is not null)
                {
                    return;
                }

                // 消去した要求に対応する通知が残っている場合があります。
                if (pending.Count == 0)
                {
                    continue;
                }

                entry = pending.Dequeue();
            }

            try
            {
                if (entry.Request is SpeechDispatchRequest request)
                {
                    await send(request, lifetime.Token).ConfigureAwait(false);
                }
                entry.Sent.TrySetResult();
            }
            catch (OperationCanceledException error)
            {
                entry.Sent.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                if (entry.ReportFailure is { } report)
                {
                    report(error);
                    entry.Sent.TrySetResult();
                }
                else
                {
                    entry.Sent.TrySetException(error);
                }
            }
        }
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try
        {
            // callbackと送信処理を所有者のロック外で終了させます。
            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                await dispatcher.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        finally
        {
            available.Dispose();
            lifetime.Dispose();
        }

        if (errors.Count == 0)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(errors);
        }
    }

    private sealed record Entry(SpeechDispatchRequest? Request,
        Action<Exception>? ReportFailure, bool Discardable)
    {
        internal TaskCompletionSource Sent
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
