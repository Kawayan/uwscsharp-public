namespace UWSCSharp.Wpf;

/// <summary>
/// 親が確認した時刻を保持し、準備完了を実行開始と読み替えません。
/// </summary>
internal sealed class SchedulerExecutionTimeline(DateTimeOffset acceptedAt)
{
    internal DateTimeOffset AcceptedAt { get; } = acceptedAt.ToUniversalTime();

    internal DateTimeOffset? StartedAt
    {
        get; private set;
    }

    internal DateTimeOffset? ResultObservedAt
    {
        get; private set;
    }

    internal DateTimeOffset? EndedAt
    {
        get; private set;
    }

    internal void ObserveStarted(DateTimeOffset instant)
    {
        if (ResultObservedAt is null)
        {
            StartedAt ??= instant.ToUniversalTime();
        }
    }

    internal void ObserveResult(DateTimeOffset instant, bool endedConfirmed)
    {
        ResultObservedAt ??= instant.ToUniversalTime();
        if (endedConfirmed)
        {
            EndedAt ??= instant.ToUniversalTime();
        }
    }
}
