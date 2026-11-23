namespace UWSCSharp.Wpf;

/// <summary>
/// 実際に受付した定義と、親で確認した実行・回収の経過を保持します。
/// </summary>
internal sealed record SchedulerHistoryRecord(
    int Version, Guid OccurrenceId, Guid Generation, Guid EntryId,
    string Title, string Path, IReadOnlyList<string> Arguments,
    SchedulerExecutionMode Mode, DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, DateTimeOffset? ResultObservedAt,
    long Revision, bool Started, SchedulerExecutionOutcome? Outcome,
    int? ExitCode, bool ResourcesRecovered, bool Forced, string? Diagnostic,
    SchedulerHistoryWindowCapture? Window = null)
{
    internal const int CurrentVersion = 3;

    internal bool IsProtected => EndedAt is null || !ResourcesRecovered;

    internal static SchedulerHistoryRecord Capture(SchedulerDispatch dispatch) =>
        new(CurrentVersion, dispatch.Request.OccurrenceId, dispatch.Generation,
            dispatch.Request.Entry.Id, dispatch.Request.Entry.Title,
            dispatch.Request.Entry.Path,
            Array.AsReadOnly(dispatch.Request.Entry.Arguments.ToArray()),
            dispatch.Request.Entry.Mode, dispatch.Request.ObservedAt.ToUniversalTime(),
            null, null, null, 0, false, null, null, false, false, null,
            dispatch.Request.WindowTrigger is { } trigger
                ? new(trigger.Target.Handle, trigger.Target.ProcessId,
                    trigger.Target.ThreadId, trigger.ObservedAt.ToUniversalTime()) : null);

    internal SchedulerHistoryRecord MarkStarted(DateTimeOffset instant) =>
        StartedAt is not null || ResultObservedAt is not null ? this : this with
        {
            StartedAt = instant.ToUniversalTime(),
            Started = true,
            Revision = Revision + 1
        };

    internal SchedulerHistoryRecord Complete(SchedulerExecutionResult result,
        DateTimeOffset instant, bool endedConfirmed) => ResultObservedAt is not null ? this : this with
        {
            ResultObservedAt = instant.ToUniversalTime(),
            EndedAt = endedConfirmed ? instant.ToUniversalTime() : null,
            Revision = Revision + 1,
            Started = Started || result.Started,
            Outcome = result.Outcome,
            ExitCode = result.ExitCode,
            ResourcesRecovered = result.ResourcesRecovered,
            Forced = result.Forced,
            Diagnostic = result.Diagnostic
        };
}

/// <summary>
/// 保存時の窓の観測です。現在の窓を解決する用途には使いません。
/// </summary>
internal sealed record SchedulerHistoryWindowCapture(
    ulong Handle, uint ProcessId, uint ThreadId, DateTimeOffset ObservedAt);
