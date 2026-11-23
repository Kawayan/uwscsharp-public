namespace UWSCSharp.Wpf;

internal enum SchedulerExecutionOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Unknown
}

/// <summary>
/// 実行結果と、同じ枠を再使用できる資源回収を区別します。
/// </summary>
internal sealed record SchedulerExecutionResult
{
    private SchedulerExecutionResult(SchedulerExecutionOutcome outcome,
        bool started, int? exitCode, bool resourcesRecovered, string? diagnostic,
        bool forced = false)
    {
        Outcome = outcome;
        Started = started;
        ExitCode = exitCode;
        ResourcesRecovered = resourcesRecovered;
        Diagnostic = diagnostic;
        Forced = forced;
    }

    internal SchedulerExecutionOutcome Outcome
    {
        get;
    }

    internal bool Started
    {
        get;
    }

    internal int? ExitCode
    {
        get;
    }

    internal bool ResourcesRecovered
    {
        get;
    }

    internal string? Diagnostic
    {
        get;
    }

    internal bool Forced
    {
        get;
    }

    internal static SchedulerExecutionResult Completed(int exitCode,
        bool started, bool cancellationCompleted, bool resourcesRecovered,
        string? diagnostic = null)
    {
        if (cancellationCompleted && exitCode != 130)
        {
            throw new ArgumentException("取消完了には終了コード130が必要です。",
                nameof(exitCode));
        }

        SchedulerExecutionOutcome outcome = cancellationCompleted
            ? SchedulerExecutionOutcome.Cancelled
            : exitCode != 0 ? SchedulerExecutionOutcome.Failed
            : !resourcesRecovered ? SchedulerExecutionOutcome.Unknown
            : SchedulerExecutionOutcome.Succeeded;
        return new(outcome, started, exitCode, resourcesRecovered, diagnostic);
    }

    internal static SchedulerExecutionResult FailedToStart(string diagnostic) =>
        new(SchedulerExecutionOutcome.Failed, false, null, true, diagnostic);

    internal static SchedulerExecutionResult Unknown(bool started,
        int? exitCode, bool resourcesRecovered, string? diagnostic = null,
        bool forced = false) =>
        new(SchedulerExecutionOutcome.Unknown, started, exitCode,
            resourcesRecovered, diagnostic, forced);
}
