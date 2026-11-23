using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識制御を直列化し、同じ評価中の入れ子要求を順に処理します。
/// </summary>
internal sealed class SpeechRecognitionControlQueue
{
    private readonly SemaphoreSlim root = new(1, 1);
    private readonly AsyncLocal<Frame?> current = new();

    internal Task WaitAsync() => root.WaitAsync();

    internal void Release() => root.Release();

    internal async Task RunAsync(Func<Task> operation, CancellationToken token)
    {
        Frame? parent = current.Value;
        SemaphoreSlim queue = parent is not null && Volatile.Read(ref parent.Active)
            ? parent.Children : root;
        await queue.WaitAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        if (parent is not null && queue == parent.Children
            && !Volatile.Read(ref parent.Active))
        {
            // 完了済みの評価文脈を引き継いだ要求は通常の待ち行列へ戻します。
            queue.Release();
            queue = root;
            await queue.WaitAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        var frame = new Frame();
        current.Value = frame;
        try
        {
            token.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
        finally
        {
            Volatile.Write(ref frame.Active, false);
            // 評価側が先に戻っても、開始済みの子要求が資源を使い終わるまで保持します。
            await frame.Children.WaitAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            frame.Children.Release();
            current.Value = parent;
            queue.Release();
        }
    }

    private sealed class Frame
    {
        internal readonly SemaphoreSlim Children = new(1, 1);
        internal bool Active = true;
    }
}
