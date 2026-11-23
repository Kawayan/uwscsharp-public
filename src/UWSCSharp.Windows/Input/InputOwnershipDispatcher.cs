namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッドから呼ぶ入力操作を提供します。
/// </summary>
internal interface IInputOwnershipBackend
{
    void SetBlocked(bool blocked);

    void Send(Native.Input input);
}

/// <summary>
/// 入力禁止と送信を単一スレッドで実行します。
/// </summary>
internal sealed class InputOwnershipDispatcher : IAsyncDisposable
{
    private sealed record Request(Action Action, TaskCompletionSource Completion);

    private readonly IInputOwnershipBackend backend;
    private readonly object gate = new();
    private readonly Queue<Request> requests = new();
    private readonly TaskCompletionSource stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing;
    private bool blocked;

    internal InputOwnershipDispatcher(IInputOwnershipBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        this.backend = backend;
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UWSC input owner"
        };
        thread.Start();
    }

    internal Task SetBlockedAsync(bool value) => Enqueue(() =>
    {
        if (blocked != value)
        {
            backend.SetBlocked(value);
            // OS操作が失敗した場合は所有状態を変更しません。
            blocked = value;
        }
    });

    internal Task SendAsync(Native.Input input) => Enqueue(() => backend.Send(input));

    private Task Enqueue(Action action)
    {
        lock (gate)
        {
            if (closing)
            {
                return Task.FromException(new ObjectDisposedException(GetType().Name));
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Enqueue(new(action, completion));
            Monitor.Pulse(gate);
            return completion.Task;
        }
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    while (!closing && requests.Count == 0)
                    {
                        Monitor.Wait(gate);
                    }

                    if (closing)
                    {
                        break;
                    }

                    request = requests.Dequeue();
                }

                try
                {
                    request.Action();
                    request.Completion.TrySetResult();
                }
                catch (Exception error)
                {
                    request.Completion.TrySetException(error);
                }
            }

            if (blocked)
            {
                backend.SetBlocked(false);
                blocked = false;
            }

        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            lock (gate)
            {
                closing = true;
                while (requests.TryDequeue(out Request? request))
                {
                    request.Completion.TrySetException(
                        new ObjectDisposedException(GetType().Name));
                }
            }

            if (failure is null)
            {
                stopped.TrySetResult();
            }
            else
            {
                stopped.TrySetException(failure);
            }
        }
    }

    /// <summary>
    /// 保留要求を終了し、実行中の操作の後で禁止を解除します。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closing = true;
            Monitor.Pulse(gate);
            return new ValueTask(stopped.Task);
        }
    }
}
