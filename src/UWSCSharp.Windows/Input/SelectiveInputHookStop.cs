namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッド上の解除結果と、回収後も通知する失敗を保持します。
/// </summary>
internal sealed record SelectiveInputStopResult(bool Stopped, Exception? Error);

/// <summary>
/// 同じ待機予算内で解除を試み、未解除の登録を呼出元へ残します。
/// </summary>
internal static class SelectiveInputHookStop
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static SelectiveInputStopResult Run(ISelectiveInputHookSession session,
        TimeProvider clock, long startedAt)
    {
        Exception? failure = null;
        bool released = false;
        try
        {
            session.PrepareStop(clock, startedAt);
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            session.Suspend();
        }
        catch (Exception error)
        {
            failure = Combine(failure, error);
        }

        int attempts = session.TakeStopAttempts();
        if (attempts == 1 && clock.GetElapsedTime(startedAt) >= Timeout)
        {
            // 登録時の初回回収が期限を使い切った場合は、追加の再試行を始めません。
            attempts = 0;
            released = !session.IsRegistered;
        }

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                session.Stop();
            }
            catch (Exception error)
            {
                failure = Combine(failure, error);
            }

            released = !session.IsRegistered;
            if (released || clock.GetElapsedTime(startedAt) >= Timeout)
            {
                break;
            }
        }

        if (!released && failure is null)
        {
            failure = WindowsOperationErrors.Record(new InvalidOperationException(
                "解除後も入力フックが登録されています。"));
        }

        return new(released, failure);
    }

    internal static Exception? Combine(Exception? first, Exception? second) =>
        first is null ? second : second is null || ReferenceEquals(first, second)
            ? first : new AggregateException(first, second);
}
