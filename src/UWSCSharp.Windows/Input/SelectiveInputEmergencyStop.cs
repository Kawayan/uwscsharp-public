namespace UWSCSharp.Windows;

/// <summary>
/// 実行の停止tokenと、世代を越えて維持する緊急停止状態を持ちます。
/// </summary>
internal sealed class SelectiveInputEmergencyStop
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly SelectiveInputEmergencyRegistry registry;
    private Func<Task>? cleanup;
    private Task? cancelling;
    private Task? disposal;
    private int active;
    private int retained;
    private int registered;
    private int requested;
    private int dispatching;
    private bool ended;
    private Exception? failure;

    internal Exception? TakeFailure() => Interlocked.Exchange(ref failure, null);

    internal SelectiveInputEmergencyStop(SelectiveInputEmergencyRegistry? registry = null)
    {
        this.registry = registry ?? SelectiveInputEmergencyRegistry.Shared;
        token = cancellation.Token;
    }

    internal CancellationToken Token => token;

    internal bool IsRequested => Volatile.Read(ref requested) != 0;

    internal bool IsDispatching => Volatile.Read(ref dispatching) != 0;

    internal SelectiveInputEmergencyRegistry Registry => registry;

    internal void Attach(Func<Task> cleanup) =>
        Volatile.Write(ref this.cleanup, cleanup);

    internal void SetActive(bool value)
    {
        Volatile.Write(ref active, value ? 1 : 0);
        if (value)
        {
            Register();
        }
    }

    internal void SetRetained(bool value)
    {
        Volatile.Write(ref retained, value ? 1 : 0);
        if (value)
        {
            Register();
        }
    }

    private void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 0)
        {
            registry.Register(this);
        }
    }

    internal void Broadcast() => registry.RequestAll();

    // callbackでは原子状態の記録と配送予約だけを行います。
    internal void Request()
    {
        if (Volatile.Read(ref active) == 0 && Volatile.Read(ref retained) == 0)
        {
            return;
        }

        Volatile.Write(ref requested, 1);
        if (Interlocked.CompareExchange(ref dispatching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static target =>
                _ = target.DispatchAsync(), this, preferLocal: false);
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            Task stop;
            lock (gate)
            {
                stop = ended ? Task.CompletedTask
                    : cancelling ??= cancellation.CancelAsync();
            }

            // 取消callbackの完了待ちより先に、既存の停止予算を開始します。
            Task recover = Volatile.Read(ref cleanup)?.Invoke() ?? Task.CompletedTask;
            try
            {
                await Task.WhenAll(stop, recover).ConfigureAwait(false);
            }
            catch
            {
                // 取消の失敗はDisposeExecutionAsync、回収の失敗は所有者が保持します。
            }
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
        }
        finally
        {
            Volatile.Write(ref dispatching, 0);
        }
    }

    internal void ThrowIfRequested()
    {
        if (IsRequested)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "緊急解除後の同じ実行では入力禁止を再開できません。"));
        }
    }

    internal void MarkExecutionEnded()
    {
        lock (gate)
        {
            ended = true;
        }
    }

    internal Task DisposeExecutionAsync()
    {
        lock (gate)
        {
            ended = true;
            return disposal ??= DisposeSourceAsync(cancelling ?? Task.CompletedTask);
        }
    }

    private async Task DisposeSourceAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
