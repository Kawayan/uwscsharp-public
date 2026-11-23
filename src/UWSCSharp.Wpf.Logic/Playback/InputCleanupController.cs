namespace UWSCSharp.Wpf;

internal sealed record InputCleanupSnapshot(
    IReadOnlyList<Exception> Errors, int PendingCount);

/// <summary>
/// 画面を占有せずに回収状態を照会し、明示要求を一つずつ処理します。
/// </summary>
/// <remarks>状態の参照と操作開始は、同じ画面スレッドから行います。</remarks>
internal sealed class InputCleanupController(
    Func<int> countPending,
    Func<Task<InputCleanupSnapshot>> retry,
    int? initialCount = 0)
{
    private Task<InputCleanupSnapshot>? operation;

    internal int PendingCount { get; private set; } = initialCount ?? 0;

    internal bool HasKnownCount { get; private set; } = initialCount.HasValue;

    internal bool IsBusy
    {
        get; private set;
    }

    internal bool CanStart => HasKnownCount && !IsBusy && PendingCount == 0;

    internal bool CanRetry => !IsBusy && (!HasKnownCount || PendingCount > 0);

    internal Task<InputCleanupSnapshot> RefreshAsync() => Start(retryRequested: false);

    internal Task<InputCleanupSnapshot> RetryAsync()
    {
        if (CanStart)
        {
            return Task.FromResult(new InputCleanupSnapshot([], 0));
        }

        return Start(retryRequested: true);
    }

    private Task<InputCleanupSnapshot> Start(bool retryRequested)
    {
        if (operation is { IsCompleted: false })
        {
            return operation;
        }

        var completion = new TaskCompletionSource<InputCleanupSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        operation = completion.Task;
        IsBusy = true;
        _ = CompleteAsync(completion, retryRequested);
        return completion.Task;
    }

    private async Task CompleteAsync(
        TaskCompletionSource<InputCleanupSnapshot> completion, bool retryRequested)
    {
        InputCleanupSnapshot result;
        try
        {
            // 公開APIが同期ロックを取得する部分も、画面スレッドの外へ送ります。
            result = retryRequested
                ? await Task.Run(retry)
                : await Task.Run(() => new InputCleanupSnapshot([], countPending()));
            if (result.PendingCount < 0)
            {
                throw new InvalidOperationException("未回収所有者数が不正です。");
            }

            PendingCount = result.PendingCount;
            HasKnownCount = true;
        }
        catch (Exception failure)
        {
            // 最後の件数を0へ変更せず、状態不明の間も開始を制限します。
            HasKnownCount = false;
            result = new InputCleanupSnapshot([failure], PendingCount);
        }
        finally
        {
            IsBusy = false;
        }

        completion.SetResult(result);
    }
}
