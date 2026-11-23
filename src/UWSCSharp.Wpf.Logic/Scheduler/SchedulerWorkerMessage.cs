namespace UWSCSharp.Wpf;

internal readonly record struct SchedulerWorkerIdentity(
    Guid Generation, Guid OccurrenceId);

internal enum SchedulerWorkerMessageKind : byte
{
    Request = 1,
    Ready,
    Start,
    Cancel,
    Completed,
    Started,
    Heartbeat,
    Keyboard
}

internal abstract record SchedulerWorkerMessage(
    SchedulerWorkerIdentity Identity)
{
    internal sealed record Request(SchedulerWorkerIdentity Identity,
        Guid EntryId, string Path, IReadOnlyList<string> Arguments,
        DateTimeOffset ObservedAt,
        SchedulerWindowTrigger? WindowTrigger = null) : SchedulerWorkerMessage(Identity)
    {
        internal static Request FromDispatch(SchedulerDispatch dispatch) =>
            new(new(dispatch.Generation, dispatch.Request.OccurrenceId),
                dispatch.Request.Entry.Id, dispatch.Request.Entry.Path,
                Array.AsReadOnly(dispatch.Request.Entry.Arguments.ToArray()),
                dispatch.Request.ObservedAt, dispatch.Request.WindowTrigger);
    }

    internal sealed record Ready(SchedulerWorkerIdentity Identity,
        int ProcessId) : SchedulerWorkerMessage(Identity);

    internal sealed record Start(SchedulerWorkerIdentity Identity)
        : SchedulerWorkerMessage(Identity);

    internal sealed record Cancel(SchedulerWorkerIdentity Identity)
        : SchedulerWorkerMessage(Identity);

    internal sealed record Started(SchedulerWorkerIdentity Identity)
        : SchedulerWorkerMessage(Identity);

    internal sealed record Heartbeat(SchedulerWorkerIdentity Identity)
        : SchedulerWorkerMessage(Identity);

    internal sealed record Keyboard(SchedulerWorkerIdentity Identity,
        Guid Transaction, int Key, SchedulerKeyboardPhase Phase,
        bool Succeeded = true, string? Diagnostic = null) : SchedulerWorkerMessage(Identity);

    internal sealed record Completed(SchedulerWorkerIdentity Identity,
        int ExitCode, bool CancellationCompleted, bool InputRecovered,
        string? Diagnostic) : SchedulerWorkerMessage(Identity);
}

internal enum SchedulerKeyboardPhase
{
    Request,
    Granted,
    Release,
    Restored
}
