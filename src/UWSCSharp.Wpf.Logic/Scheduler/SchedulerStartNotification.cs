namespace UWSCSharp.Wpf;

/// <summary>
/// 実行開始の通知を一度だけ試み、失敗を実行から分離します。
/// </summary>
internal sealed class SchedulerStartNotification(bool enabled, Action notify)
{
    private int attempted;
    private Exception? error;

    internal Exception? Error => Volatile.Read(ref error);

    internal bool NotifyStarted()
    {
        if (Interlocked.Exchange(ref attempted, 1) != 0 || !enabled)
        {
            return false;
        }
        try
        {
            notify();
        }
        catch (Exception failure)
        {
            Volatile.Write(ref error, failure);
        }
        return true;
    }
}
