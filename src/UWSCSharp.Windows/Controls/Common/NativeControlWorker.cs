namespace UWSCSharp.Windows;

internal static class NativeControlWorker
{
    private static readonly TimeSpan DefaultWorkTimeout = TimeSpan.FromSeconds(1);

    private static readonly SemaphoreSlim Slots = new(4);

    internal static int Active => 4 - Slots.CurrentCount;

    internal static async Task<T> RunAsync<T>(Func<CancellationToken, T> operation,
        CancellationToken token, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? DefaultWorkTimeout;
        if (!await Slots.WaitAsync(limit, token).ConfigureAwait(false))
        {
            throw WindowsOperationErrors.Record(
                new TimeoutException("ネイティブcontrolの待機枠が満杯です。"));
        }

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(limit);
        CancellationToken activeToken = lifetime.Token;
        Task<T> pending = Task.Factory.StartNew(() =>
        {
            try
            {
                activeToken.ThrowIfCancellationRequested();
                return operation(activeToken);
            }
            finally
            {
                lifetime.Dispose();
                Slots.Release();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        // 呼出し側の取消やtimeout後も、同期message完了までworkerが所有します。
        _ = pending.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        try
        {
            return await pending.WaitAsync(limit, token).ConfigureAwait(false);
        }
        catch (TimeoutException failure) when (
            !ReferenceEquals(pending.Exception?.InnerException, failure))
        {
            // WaitAsync自身の期限超過だけを識別し、操作内の同型例外は区別します。
            throw WindowsOperationErrors.Record(failure);
        }
        catch (OperationCanceledException failure) when (
            !token.IsCancellationRequested
            && failure.CancellationToken == activeToken
            && activeToken.IsCancellationRequested)
        {
            throw WindowsOperationErrors.Record(
                new TimeoutException(
                    "ネイティブcontrolの応答待ちが期限を超えました。", failure));
        }
    }
}
