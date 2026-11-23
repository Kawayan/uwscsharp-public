namespace UWSCSharp.Windows;

/// <summary>
/// 実行終了後も未回収の所有者を保持し、明示的な再回収を共有します。
/// </summary>
internal sealed class SelectiveInputCleanupRegistry
{
    private readonly object gate = new();
    private readonly HashSet<SelectiveInputRunOwner> pending = [];
    private Task<WindowsSelectiveInputCleanupResult>? retry;

    internal int PendingCount
    {
        get
        {
            lock (gate)
            {
                return CountPendingCore();
            }
        }
    }

    internal void Retain(SelectiveInputRunOwner owner)
    {
        lock (gate)
        {
            if (!owner.IsCleanupComplete)
            {
                pending.Add(owner);
            }
        }
    }

    internal Task<WindowsSelectiveInputCleanupResult> RetryAsync()
    {
        lock (gate)
        {
            if (retry is null || retry.IsCompleted)
            {
                CountPendingCore();
                retry = RetryCoreAsync([.. pending]);
            }

            return retry;
        }
    }

    private async Task<WindowsSelectiveInputCleanupResult> RetryCoreAsync(
        SelectiveInputRunOwner[] owners)
    {
        List<Exception> errors = [];
        foreach (SelectiveInputRunOwner owner in owners)
        {
            try
            {
                await owner.RetryCleanupAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 既に解除できていても、保持された過去の障害は診断結果へ残します。
                errors.Add(error);
            }

            lock (gate)
            {
                if (owner.IsCleanupComplete)
                {
                    pending.Remove(owner);
                }
            }
        }

        lock (gate)
        {
            return new WindowsSelectiveInputCleanupResult(
                errors.AsReadOnly(), CountPendingCore());
        }
    }

    private int CountPendingCore()
    {
        pending.RemoveWhere(static owner => owner.IsCleanupComplete);
        return pending.Count;
    }
}
