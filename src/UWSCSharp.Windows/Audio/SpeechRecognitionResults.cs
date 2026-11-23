using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 認識結果を1件保持し、取得方式ごとの消去を扱います。
/// </summary>
internal sealed class SpeechRecognitionResults : IDisposable
{
    private readonly object gate = new();
    private TaskCompletionSource changed = NewSignal();
    private string latest = string.Empty;
    private bool disposed;
    private ExceptionDispatchInfo? failure;

    /// <summary>
    /// 前の結果を置き換えます。空文字列は待機を完了させません。
    /// </summary>
    internal void Publish(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        TaskCompletionSource signal;
        lock (gate)
        {
            ThrowIfUnavailable();
            latest = text;
            signal = changed;
            changed = NewSignal();
        }

        signal.TrySetResult();
    }

    /// <summary>
    /// 保持した結果を返し、返却用に読み取った欄を消去します。
    /// </summary>
    internal string TakeLatest()
    {
        lock (gate)
        {
            ThrowIfUnavailable();
            string result = latest;
            latest = string.Empty;
            return result;
        }
    }

    /// <summary>
    /// 既存結果を消して次の結果を待ち、取得後は欄を保持します。
    /// </summary>
    internal Task<string> WaitForNextAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfUnavailable();
            token.ThrowIfCancellationRequested();
            latest = string.Empty;
            return WaitCoreAsync(changed.Task, token);
        }
    }

    private async Task<string> WaitCoreAsync(Task notification,
        CancellationToken token)
    {
        while (true)
        {
            await notification.WaitAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfUnavailable();
                token.ThrowIfCancellationRequested();
                if (latest.Length != 0)
                {
                    return latest;
                }

                // 他の取得が欄を消した場合も、次の通知を取り逃がさず待ちます。
                notification = changed.Task;
            }
        }
    }

    /// <summary>
    /// 最初の通知失敗を保持し、待機中と後続の取得へ伝えます。
    /// </summary>
    internal void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        TaskCompletionSource signal;
        lock (gate)
        {
            if (disposed || failure is not null)
            {
                return;
            }

            failure = ExceptionDispatchInfo.Capture(error);
            latest = string.Empty;
            signal = changed;
        }

        signal.TrySetResult();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        failure?.Throw();
    }

    public void Dispose()
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            latest = string.Empty;
            signal = changed;
        }

        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
