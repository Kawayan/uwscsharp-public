namespace UWSCSharp.Core;

/// <summary>
/// 実行単位で協調的な一時停止と再開を管理します。
/// </summary>
public sealed class ScriptPauseControl : IDisposable
{
    private static readonly TimeSpan MaximumDelaySlice = TimeSpan.FromMilliseconds(10);

    private readonly object sync = new();
    private TaskCompletionSource? resume;
    private int waiting;
    private bool disposed;

    /// <summary>
    /// 一時停止が要求されているかを取得します。
    /// </summary>
    public bool IsPauseRequested
    {
        get
        {
            lock (sync)
            {
                return resume is not null;
            }
        }
    }

    /// <summary>
    /// 実行が確認点で待機しているかを取得します。
    /// </summary>
    public bool IsPaused => Volatile.Read(ref waiting) != 0;

    /// <summary>
    /// 次の確認点で一時停止するよう要求します。
    /// </summary>
    public void Pause()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// 待機中の実行を再開します。
    /// </summary>
    public void Resume()
    {
        TaskCompletionSource? pending;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = resume;
            resume = null;
        }

        pending?.TrySetResult();
    }

    /// <summary>
    /// ワーカーを占有せず、再開または取消を待ちます。
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? pending;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                pending = resume?.Task;
            }

            if (pending is null)
            {
                return;
            }

            Interlocked.Increment(ref waiting);
            try
            {
                await pending.WaitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref waiting);
            }

            // 再開直後の次の一時停止要求も再確認します。
        }
    }

    /// <summary>
    /// 一時停止時間を除いて指定時間待機します。
    /// </summary>
    public async Task DelayAsync(TimeSpan duration,
        CancellationToken token)
    {
        await DelayAsync(duration, token, Task.Delay).ConfigureAwait(false);
    }

    internal async Task DelayAsync(TimeSpan duration,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<bool>? canPause = null,
        TimeProvider? timeProvider = null)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        TimeSpan remaining = duration;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (canPause?.Invoke() ?? true)
            {
                await WaitAsync(token).ConfigureAwait(false);
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            TimeSpan slice = remaining < MaximumDelaySlice
                ? remaining : MaximumDelaySlice;
            long started = clock.GetTimestamp();
            await delay(slice, token).ConfigureAwait(false);
            // 要求直後の最大一刻み分だけは経過時間として扱います。
            remaining -= clock.GetElapsedTime(started);
        }

    }

    /// <summary>
    /// 実行終了後に待機資源を解放します。
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource? pending;
        lock (sync)
        {
            disposed = true;
            pending = resume;
            resume = null;
        }

        pending?.TrySetResult();
    }
}
