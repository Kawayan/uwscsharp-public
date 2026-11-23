namespace UWSCSharp.Core;

/// <summary>
/// 通知元を待たせたまま、登録元の実行経路へ処理を渡します。
/// </summary>
internal sealed class ScriptComEventDispatcher
{
    private sealed class Request(Func<ValueTask<IReadOnlyList<Value>>> callback,
        CancellationToken token)
    {
        private int state;
        internal readonly TaskCompletionSource<IReadOnlyList<Value>> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CancelPending()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                Completion.TrySetCanceled(token);
            }
        }

        internal async ValueTask RunAsync()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<Value> values = await callback()
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                token.ThrowIfCancellationRequested();
                Completion.TrySetResult(values);
            }
            catch (OperationCanceledException failure)
            {
                Completion.TrySetCanceled(failure.CancellationToken);
                throw;
            }
            catch (Exception failure)
            {
                Completion.TrySetException(failure);
                throw;
            }
            finally
            {
                Volatile.Write(ref state, 2);
            }
        }
    }

    private readonly object gate = new();
    private readonly Queue<Request> pending = new();
    private bool closed;

    internal async ValueTask<IReadOnlyList<Value>> EnqueueAsync(
        Func<ValueTask<IReadOnlyList<Value>>> callback, CancellationToken token)
    {
        var request = new Request(callback, token);
        using CancellationTokenRegistration registration = token.Register(
            static value => ((Request)value!).CancelPending(), request);
        lock (gate)
        {
            if (closed)
            {
                request.CancelPending();
            }
            else
            {
                pending.Enqueue(request);
            }
        }

        return await request.Completion.Task.ConfigureAwait(false);
    }

    internal async ValueTask DispatchOneAsync()
    {
        Request? request;
        lock (gate)
        {
            pending.TryDequeue(out request);
        }

        if (request is not null)
        {
            await request.RunAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            while (pending.TryDequeue(out Request? request))
            {
                request.CancelPending();
            }
        }
    }
}
