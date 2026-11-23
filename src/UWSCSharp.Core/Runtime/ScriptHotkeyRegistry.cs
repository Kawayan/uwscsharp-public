using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

/// <summary>
/// OS側の登録と、実行側の非再入キューを対応付けます。
/// </summary>
/// <remarks>登録・解除・配送・終了は同じ実行経路から呼び出します。</remarks>
internal sealed class ScriptHotkeyRegistry(
    IScriptHotkeySource source, int capacity, Action reportOverflow) : IAsyncDisposable
{
    private readonly ScriptCallbackQueue<ScriptHotkey> callbacks = new(capacity);
    private readonly Dictionary<ScriptHotkey,
        ScriptCallbackQueue<ScriptHotkey>.Registration> registrations = [];
    private readonly object publicationGate = new();
    private bool closed;

    internal Task Ready => callbacks.Ready;

    internal void ThrowIfFaulted()
    {
        if (source.Failure is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>
    /// 登録の成功後にだけ旧世代の保留通知を破棄します。
    /// </summary>
    internal bool TryRegister(ScriptHotkey key,
        Func<ScriptHotkey, CancellationToken, ValueTask> callback)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        ThrowIfFaulted();
        ScriptCallbackQueue<ScriptHotkey>.Registration candidate = callbacks.Register(callback);
        bool accepted = false;
        bool decided = false;
        int staged = 0;
        bool stagedOverflow = false;
        object candidateGate = new();
        try
        {
            accepted = source.TryRegister(key, () =>
            {
                lock (candidateGate)
                {
                    if (!decided)
                    {
                        // 登録失敗時には、先行通知も容量超過も外へ確定しません。
                        if (staged < capacity)
                        {
                            staged++;
                        }
                        else
                        {
                            stagedOverflow = true;
                        }
                    }
                    else if (accepted)
                    {
                        Publish();
                    }
                }
            });
            if (!accepted)
            {
                return false;
            }

            if (registrations.Remove(key, out ScriptCallbackQueue<ScriptHotkey>.Registration? previous))
            {
                callbacks.Unregister(previous);
            }

            registrations.Add(key, candidate);
            lock (candidateGate)
            {
                decided = true;
                for (int index = 0; index < staged; index++)
                {
                    Publish();
                }

                if (stagedOverflow)
                {
                    lock (publicationGate)
                    {
                        reportOverflow();
                    }
                }
            }

            return true;
        }
        finally
        {
            if (!accepted)
            {
                lock (candidateGate)
                {
                    decided = true;
                }

                callbacks.Unregister(candidate);
            }
        }

        void Publish()
        {
            lock (publicationGate)
            {
                if (callbacks.Publish(candidate, key)
                    == ScriptCallbackQueue<ScriptHotkey>.PublishResult.Full)
                {
                    reportOverflow();
                }
            }
        }
    }

    /// <summary>
    /// OS側で解除できた場合だけ保留通知を破棄します。
    /// </summary>
    internal bool TryUnregister(ScriptHotkey key)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        ThrowIfFaulted();
        if (!registrations.TryGetValue(key,
            out ScriptCallbackQueue<ScriptHotkey>.Registration? registration))
        {
            return true;
        }

        if (!source.TryUnregister(key))
        {
            return false;
        }

        registrations.Remove(key);
        callbacks.Unregister(registration);
        return true;
    }

    internal ValueTask<bool> DispatchOneAsync(CancellationToken token) =>
        callbacks.DispatchOneAsync(token);

    /// <summary>
    /// 先に通知受付を停止し、OS側の解放が失敗しても再配送を防ぎます。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        // 容量超過の報告まで完了してから受付を閉じ、終了時の見落としを防ぎます。
        lock (publicationGate)
        {
            callbacks.Close();
        }
        registrations.Clear();
        Exception? primary = source.Failure;
        try
        {
            await source.DisposeAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
        catch (Exception cleanup) when (primary is not null
            && !ReferenceEquals(primary, cleanup))
        {
            primary.Data["UWSCSharp.HotkeyCleanupErrors"] = cleanup;
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        ThrowIfFaulted();
    }
}
