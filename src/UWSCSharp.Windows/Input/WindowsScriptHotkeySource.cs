using System.Runtime.ExceptionServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 専用スレッドでキーの登録・受信・解除を直列に処理します。
/// </summary>
internal sealed class WindowsScriptHotkeySource : IScriptHotkeySource
{
    private readonly IScriptHotkeyPlatform platform;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Queue<Work> pending = new();
    private readonly Dictionary<ScriptHotkey, int> ids = [];
    private readonly Dictionary<int, Action> callbacks = [];
    private readonly TaskCompletionSource ready = NewSignal();
    private readonly TaskCompletionSource ended = NewSignal();
    private readonly Thread thread;
    private ExceptionDispatchInfo? failure;
    private int nextId;
    private bool closing;
    private bool notificationsDrained;
    private Task? disposal;

    internal WindowsScriptHotkeySource(IScriptHotkeyPlatform platform)
    {
        this.platform = platform;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UWSC# script hotkeys"
        };
        thread.Start();
    }

    public Exception? Failure => Volatile.Read(ref failure)?.SourceException;

    internal bool IsOwnerThreadAlive => thread.IsAlive;

    internal int PendingRequestCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    public bool TryRegister(ScriptHotkey key, Action notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        if (key.VirtualKey is < 0 or > 255 || key.Modifiers is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        if (SelectiveInputEmergencyShortcut.IsReserved(key))
        {
            return false;
        }

        return Invoke(() =>
        {
            if (ids.TryGetValue(key, out int previous))
            {
                if (!notificationsDrained)
                {
                    return false;
                }

                callbacks[previous] = notify;
                return true;
            }

            if (nextId == 0xBFFF)
            {
                throw new InvalidOperationException("ホットキー登録IDを使い切りました。");
            }

            int id = nextId + 1;
            if (!platform.Register(id, key))
            {
                return false;
            }

            nextId = id;
            ids.Add(key, id);
            callbacks.Add(id, notify);
            return true;
        });
    }

    public bool TryUnregister(ScriptHotkey key) => Invoke(() =>
    {
        if (!ids.TryGetValue(key, out int id))
        {
            return true;
        }

        if (!platform.Unregister(id))
        {
            return false;
        }

        ids.Remove(key);
        callbacks.Remove(id);
        return true;
    });

    private bool Invoke(Func<bool> action)
    {
        if (Thread.CurrentThread == thread)
        {
            throw new InvalidOperationException("通知中に登録・解除へ再入できません。");
        }

        ready.Task.GetAwaiter().GetResult();
        var work = new Work(action);
        lock (gate)
        {
            failure?.Throw();
            ObjectDisposedException.ThrowIf(closing, this);
            pending.Enqueue(work);
            wake.Set();
        }

        return work.Result.Task.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        if (Thread.CurrentThread == thread)
        {
            throw new InvalidOperationException("通知中に通知元自身を解放できません。");
        }

        lock (gate)
        {
            if (disposal is null)
            {
                closing = true;
                wake.Set();
                disposal = FinishAsync();
            }

            return new ValueTask(disposal);
        }
    }

    private async Task FinishAsync()
    {
        await ended.Task.ConfigureAwait(false);
        thread.Join();
        wake.Dispose();
        failure?.Throw();
    }

    private void Run()
    {
        Work? active = null;
        try
        {
            platform.Initialize();
            ready.TrySetResult();
            while (true)
            {
                lock (gate)
                {
                    if (closing)
                    {
                        break;
                    }

                    active = pending.Count == 0 ? null : pending.Dequeue();
                }

                // 通知が続いても、登録要求と終了要求を確認する機会を残します。
                notificationsDrained = false;
                for (int index = 0; index <= 64; index++)
                {
                    if (!platform.TryRead(out int id))
                    {
                        notificationsDrained = true;
                        break;
                    }

                    // 境界の1件も旧登録へ届け、残件がある状態では置換しません。
                    if (callbacks.TryGetValue(id, out Action? notify))
                    {
                        notify();
                    }
                }

                bool hadWork = active is not null;
                active?.Run();
                active = null;
                if (!hadWork && notificationsDrained)
                {
                    platform.Wait(wake);
                }
            }
        }
        catch (Exception error)
        {
            RecordFailure(error);
        }
        finally
        {
            foreach (int id in callbacks.Keys)
            {
                try
                {
                    if (!platform.Unregister(id))
                    {
                        throw new InvalidOperationException("ホットキーの終了時解除に失敗しました。");
                    }
                }
                catch (Exception error)
                {
                    RecordFailure(error);
                }
            }

            try
            {
                platform.Shutdown();
            }
            catch (Exception error)
            {
                RecordFailure(error);
            }

            callbacks.Clear();
            ids.Clear();
            lock (gate)
            {
                closing = true;
                Exception error = Failure ?? new ObjectDisposedException(nameof(WindowsScriptHotkeySource));
                active?.Result.TrySetException(error);
                ready.TrySetException(error);
                while (pending.TryDequeue(out Work? work))
                {
                    work.Result.TrySetException(error);
                }
            }

            ended.TrySetResult();
        }
    }

    private void RecordFailure(Exception error)
    {
        Exception? previous = Failure;
        Volatile.Write(ref failure, ExceptionDispatchInfo.Capture(previous is null
            ? error : new AggregateException(previous, error)));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Work(Func<bool> action)
    {
        internal readonly TaskCompletionSource<bool> Result = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Run()
        {
            try
            {
                Result.SetResult(action());
            }
            catch (Exception error)
            {
                Result.SetException(error);
            }
        }
    }
}
