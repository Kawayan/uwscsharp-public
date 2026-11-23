namespace UWSCSharp.Core;

/// <summary>
/// 呼出し元の実行経路で通知を一件ずつ配送します。
/// </summary>
internal sealed class ScriptCallbackQueue<T>(int capacity)
{
    internal sealed class Registration(
        Func<T, CancellationToken, ValueTask> callback)
    {
        internal Func<T, CancellationToken, ValueTask> Callback { get; } = callback;
    }

    internal enum PublishResult
    {
        Accepted,
        Inactive,
        Full
    }

    private readonly object gate = new();
    private readonly int capacity = capacity > 0 ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly HashSet<Registration> registrations = [];
    private readonly Queue<(Registration Registration, T Payload)> pending = [];
    private TaskCompletionSource ready = CreateSignal();
    private bool dispatching;
    private bool closed;

    /// <summary>
    /// 通知の到着または受付終了を待つためのTaskを取得します。
    /// </summary>
    internal Task Ready
    {
        get
        {
            lock (gate)
            {
                return ready.Task;
            }
        }
    }

    /// <summary>
    /// 登録の実体を作成し、古い登録と区別します。
    /// </summary>
    internal Registration Register(Func<T, CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var registration = new Registration(callback);
            registrations.Add(registration);
            return registration;
        }
    }

    /// <summary>
    /// 登録と、その登録に属する未実行の通知を取り除きます。
    /// </summary>
    internal void Unregister(Registration registration)
    {
        lock (gate)
        {
            if (!registrations.Remove(registration))
            {
                return;
            }

            int count = pending.Count;
            for (int index = 0; index < count; index++)
            {
                (Registration owner, T payload) = pending.Dequeue();
                if (!ReferenceEquals(owner, registration))
                {
                    pending.Enqueue((owner, payload));
                }
            }

            ResetSignalIfEmpty();
        }
    }

    /// <summary>
    /// 通知を蓄えます。満杯の場合の公開仕様は呼出し側で決定します。
    /// </summary>
    internal PublishResult Publish(Registration registration, T payload)
    {
        lock (gate)
        {
            if (closed || !registrations.Contains(registration))
            {
                return PublishResult.Inactive;
            }

            if (pending.Count >= capacity)
            {
                return PublishResult.Full;
            }

            pending.Enqueue((registration, payload));
            ready.TrySetResult();
            return PublishResult.Accepted;
        }
    }

    /// <summary>
    /// 一件を配送します。配送中の再呼出しでは何も実行しません。
    /// </summary>
    internal async ValueTask<bool> DispatchOneAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Registration registration;
        T payload;
        lock (gate)
        {
            if (closed || dispatching || pending.Count == 0)
            {
                return false;
            }

            (registration, payload) = pending.Dequeue();
            dispatching = true;
            ResetSignalIfEmpty();
        }

        try
        {
            await registration.Callback(payload, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            return true;
        }
        finally
        {
            lock (gate)
            {
                dispatching = false;
            }
        }
    }

    /// <summary>
    /// 新しい通知を拒否し、未実行の通知を破棄します。
    /// </summary>
    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            registrations.Clear();
            pending.Clear();
            ready.TrySetResult();
        }
    }

    private void ResetSignalIfEmpty()
    {
        if (!closed && pending.Count == 0 && ready.Task.IsCompleted)
        {
            ready = CreateSignal();
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
