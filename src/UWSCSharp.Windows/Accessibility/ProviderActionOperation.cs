namespace UWSCSharp.Windows;

/// <summary>
/// provider操作を所有スレッドから分離し、呼戻しを処理します。
/// </summary>
internal static class ProviderActionOperation
{
    private static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromSeconds(10);

    internal static Task<T> RunAsync<T>(nint root,
        Func<CancellationToken, T> operation, CancellationToken token,
        TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        uint current = Native.GetCurrentThreadId();
        bool ownsWindow = root != 0
            && Native.GetWindowThreadProcessId(root, out _) == current;
        if (root != 0 && !ownsWindow)
        {
            Native.EnumChildWindows(root, (child, parameter) =>
            {
                ownsWindow = Native.GetWindowThreadProcessId(child, out _) == current;
                return !ownsWindow;
            }, 0);
        }

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<T> pending = NativeControlWorker.RunAsync(operation, lifetime.Token,
            timeout ?? DefaultActionTimeout);
        if (!ownsWindow)
        {
            return CompleteAsync(pending, lifetime);
        }

        try
        {
            while (!pending.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1))
                {
                    if (message.Id == 0x12)
                    {
                        // 呼出し元の終了要求を消費せず、外側のloopへ戻します。
                        Native.PostQuitMessage(unchecked((int)message.WParam));
                        throw new OperationCanceledException(
                            "provider操作の待機中に終了が要求されました。");
                    }

                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
                else
                {
                    token.WaitHandle.WaitOne(1);
                }
            }

            return Task.FromResult(pending.GetAwaiter().GetResult());
        }
        catch
        {
            lifetime.Cancel();
            // 呼出し側が終了しても、完了後の例外を未観測にしません。
            _ = pending.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            throw;
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private static async Task<T> CompleteAsync<T>(Task<T> pending,
        CancellationTokenSource lifetime)
    {
        using (lifetime)
        {
            return await pending.ConfigureAwait(false);
        }
    }
}
