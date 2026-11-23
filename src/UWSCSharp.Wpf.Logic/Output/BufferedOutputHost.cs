using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

/// <summary>
/// スクリプトの出力を溜め、UIスレッドへまとめて反映させます。
/// 未反映の文字数が上限に達した場合は、反映されるまで書込み側を待機させます。
/// </summary>
internal sealed class BufferedOutputHost : IScriptHost
{
    private readonly object gate = new();
    private readonly Action requestFlush;
    private readonly int capacity;
    private List<string> pending = [];
    private long pendingLength;
    private bool flushRequested;
    private TaskCompletionSource? drained;

    /// <summary>
    /// 反映依頼の方法と、待機を始める未反映文字数を指定します。
    /// </summary>
    public BufferedOutputHost(Action requestFlush, int capacity = 100_000)
    {
        ArgumentNullException.ThrowIfNull(requestFlush);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.requestFlush = requestFlush;
        this.capacity = capacity;
    }

    /// <summary>
    /// 出力を溜め、必要な場合だけ反映を依頼します。
    /// </summary>
    public async ValueTask WriteOutputAsync(string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        long length = (long)text.Length + Environment.NewLine.Length;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait = null;
            bool request = false;
            lock (gate)
            {
                // 空のときは上限を超える1行も受け付け、待機が解けない状態を避けます。
                if (pending.Count == 0 || pendingLength + length <= capacity)
                {
                    pending.Add(text);
                    // 空行も容量へ数え、表示時に加える改行を含めます。
                    pendingLength += length;
                    request = !flushRequested;
                    flushRequested = true;
                }
                else
                {
                    drained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = drained.Task;
                }
            }

            if (wait is null)
            {
                if (request)
                {
                    requestFlush();
                }

                return;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 溜まった出力を取り出し、待機中の書込みを再開させます。
    /// </summary>
    public IReadOnlyList<string> Drain()
    {
        List<string> lines;
        TaskCompletionSource? waiting;
        lock (gate)
        {
            lines = pending;
            pending = [];
            pendingLength = 0;
            flushRequested = false;
            waiting = drained;
            drained = null;
        }

        waiting?.TrySetResult();
        return lines;
    }

    /// <summary>
    /// キャンセルに対応して指定時間待機します。
    /// </summary>
    public Task DelayAsync(TimeSpan duration,
        CancellationToken cancellationToken) => Task.Delay(
        duration, cancellationToken);
}
