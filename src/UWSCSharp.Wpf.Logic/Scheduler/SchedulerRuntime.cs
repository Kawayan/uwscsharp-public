using System.Collections.ObjectModel;
using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 監視所有者が直列に操作する受付処理です。OS起動は引渡し先が所有します。
/// </summary>
internal sealed partial class SchedulerRuntime
{
    private readonly SchedulerSettingsController controller;
    private readonly Func<bool> ownsMonitor;
    private readonly SchedulerMinuteGate minutes;
    private readonly SchedulerPendingQueue queue = new();
    private readonly Dictionary<Guid, string> errors = [];
    private readonly HashSet<Guid> reportedResults = [];
    private SchedulerSettings? applied;
    private SchedulerCalendar? calendar;
    private bool ownershipLost;
    private Guid configurationEpoch;

    internal SchedulerRuntime(SchedulerSettingsController controller, Func<bool> ownsMonitor,
        SchedulerApplicationSession? applicationSession = null)
    {
        this.controller = controller;
        this.ownsMonitor = ownsMonitor;
        minutes = new SchedulerMinuteGate(applicationSession);
    }

    internal bool IsActive => minutes.IsActive;

    internal int PendingCount => queue.PendingCount;

    internal int RunningCount => queue.RunningCount;

    internal Exception? LastError
    {
        get; private set;
    }

    internal SchedulerMinuteObservation? LastObservation
    {
        get; private set;
    }

    internal IReadOnlyDictionary<Guid, string> RowErrors =>
        new ReadOnlyDictionary<Guid, string>(errors);

    internal bool Start(DateTimeOffset instant, TimeZoneInfo zone)
    {
        try
        {
            RequireOwner();
            if (controller.Current is null)
            {
                controller.Reload();
            }

            SchedulerSettings settings = Current();
            var plan = new SchedulerCalendar(settings);
            Cancel(queue.Reconcile(settings));
            if (!settings.Enabled)
            {
                minutes.Stop();
                return false;
            }

            SchedulerOneShotState[] restored = [.. Current().OneShotStates.Select(state =>
                state.Status is SchedulerOneShotStatus.Accepted or SchedulerOneShotStatus.Started
                    && !queue.ContainsOccurrence(state.OccurrenceId)
                    ? state.WithStatus(SchedulerOneShotStatus.Unknown) : state)];
            if (!restored.SequenceEqual(Current().OneShotStates))
            {
                SaveStates(restored);
            }

            RequireOwner();
            applied = Current();
            calendar = plan;
            errors.Clear();
            LastError = null;
            if (!minutes.IsActive)
            {
                windowGeneration = Guid.NewGuid();
            }
            ConfigureWindows(applied);
            bool startup = minutes.Start(instant, zone);
            LastObservation = new SchedulerMinuteObservation(SchedulerMinuteStatus.Waiting,
                SchedulerCalendar.LocalMinute(instant, zone));
            if (startup)
            {
                foreach (SchedulerEntry? entry in applied.Slots)
                {
                    if (entry is not null && entry.Enabled && entry.Condition is SchedulerStartupCondition)
                    {
                        Publish(new SchedulerExecutionRequest(Guid.NewGuid(), entry, instant.ToUniversalTime()));
                    }
                }
            }

            return true;
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            return false;
        }
    }

    internal bool Reload(DateTimeOffset instant, TimeZoneInfo zone)
    {
        try
        {
            RequireOwner();
            controller.Reload();
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            return false;
        }

        return Start(instant, zone);
    }

    internal SchedulerMinuteObservation Tick(DateTimeOffset instant, TimeZoneInfo zone)
    {
        try
        {
            RequireOwner();
            SchedulerSettings settings = minutes.IsActive
                ? RefreshConfiguration() : Current();

            LastObservation = minutes.Observe(instant, zone);
            if (LastObservation.Status == SchedulerMinuteStatus.TimeZoneChanged)
            {
                Fail(new InvalidOperationException("時間帯が変更されました。確認して監視を再開してください。"));
            }

            if (LastObservation.Status != SchedulerMinuteStatus.Due)
            {
                return LastObservation;
            }

            var accepted = new List<SchedulerExecutionRequest>();
            var consumed = new HashSet<Guid>(settings.OneShotStates.Select(state => state.EntryId));
            foreach (SchedulerEntry? entry in settings.Slots)
            {
                if (entry is null || !entry.Enabled || !queue.CanAccept(entry.Id))
                {
                    continue;
                }

                SchedulerMatch result = calendar!.Evaluate(entry.Id, LastObservation.Minute,
                    consumed.Contains(entry.Id));
                if (result.Kind == SchedulerMatchKind.Error)
                {
                    errors[entry.Id] = result.Error!;
                }
                else
                {
                    errors.Remove(entry.Id);
                }

                if (result.Kind == SchedulerMatchKind.Match)
                {
                    accepted.Add(new SchedulerExecutionRequest(Guid.NewGuid(), entry, instant.ToUniversalTime()));
                }
            }

            SchedulerOneShotState[] additional = [.. accepted
                .Where(request => request.Entry.Condition is SchedulerOneShotCondition)
                .Select(request =>
                {
                    var condition = (SchedulerOneShotCondition)request.Entry.Condition;
                    return new SchedulerOneShotState(request.Entry.Id, condition.Date, condition.Time,
                        request.OccurrenceId, request.ObservedAt, SchedulerOneShotStatus.Accepted);
                })];
            if (additional.Length != 0)
            {
                SaveStates(settings.OneShotStates.Concat(additional));
            }

            RequireOwner();
            foreach (SchedulerExecutionRequest request in accepted)
            {
                Publish(request);
            }
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            LastObservation = new SchedulerMinuteObservation(SchedulerMinuteStatus.Inactive,
                SchedulerCalendar.LocalMinute(instant, zone));
        }

        return LastObservation!;
    }

    internal SchedulerExecutionRequest? TryTake(bool externalSameProcessBusy = false,
        bool allowSeparateProcess = true)
    {
        SchedulerExecutionRequest? selected = null;
        try
        {
            RequireOwner();
            _ = Current();
            if (!minutes.IsActive)
            {
                return null;
            }

            _ = RefreshConfiguration();
            if (!minutes.IsActive)
            {
                return null;
            }

            selected = queue.TryTake(externalSameProcessBusy, allowSeparateProcess);
            if (selected is not null)
            {
                if (selected.Entry.Condition is SchedulerOneShotCondition
                    && !Current().OneShotStates.Any(state => state.EntryId == selected.Entry.Id
                        && state.OccurrenceId == selected.OccurrenceId
                        && state.Status == SchedulerOneShotStatus.Accepted))
                {
                    throw new InvalidOperationException("一回予約の受付済み状態と引渡し要求が一致しません。");
                }

                Transition(selected.OccurrenceId, SchedulerOneShotStatus.Started);
                RequireOwner();
            }

            return selected;
        }
        catch (Exception error) when (Operational(error))
        {
            if (selected is not null)
            {
                queue.Complete(selected.OccurrenceId);
            }

            Fail(error);
            return null;
        }
    }

    internal bool Complete(Guid occurrenceId, bool succeeded)
    {
        if (reportedResults.Contains(occurrenceId))
        {
            return false;
        }

        bool saved = ReportResult(occurrenceId, SchedulerExecutionResult.Completed(
            succeeded ? 0 : 1, started: true, cancellationCompleted: false,
            resourcesRecovered: true));
        if (saved || reportedResults.Contains(occurrenceId))
        {
            ReleaseRecovered(occurrenceId);
        }

        return saved;
    }

    internal bool ReportResult(Guid occurrenceId, SchedulerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            RequireOwner();
            if (queue.FindRunning(occurrenceId) is null
                || !reportedResults.Add(occurrenceId))
            {
                return false;
            }

            SchedulerOneShotStatus status = result.Outcome switch
            {
                SchedulerExecutionOutcome.Succeeded => SchedulerOneShotStatus.Succeeded,
                SchedulerExecutionOutcome.Failed => SchedulerOneShotStatus.Failed,
                SchedulerExecutionOutcome.Cancelled => result.Started
                    ? SchedulerOneShotStatus.Failed : SchedulerOneShotStatus.Cancelled,
                _ => SchedulerOneShotStatus.Unknown
            };
            Transition(occurrenceId, status);
            return true;
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
            return false;
        }
    }

    internal bool ReleaseRecovered(Guid occurrenceId)
    {
        return reportedResults.Remove(occurrenceId) && queue.Complete(occurrenceId);
    }

    internal void Stop()
    {
        minutes.Stop();
        configurationEpoch = Guid.Empty;
        try
        {
            RequireOwner();
            Cancel(queue.CancelAll());
        }
        catch (Exception error) when (Operational(error))
        {
            Fail(error);
        }
    }

    private SchedulerSettings Current()
    {
        if (controller.LoadError is not null || controller.Current is null)
        {
            throw new InvalidOperationException("予約設定の再読込みまたは復旧が必要です。", controller.LoadError);
        }

        return controller.Current.Settings;
    }

    private bool SameConfiguration(SchedulerSettings settings) => applied is not null
        && settings.Enabled == applied.Enabled && settings.Beep == applied.Beep
        && settings.WakeUp == applied.WakeUp
        && settings.Slots.Count == applied.Slots.Count
        && settings.Slots.Zip(applied.Slots).All(pair => SameEntry(pair.First, pair.Second));

    private static bool SameEntry(SchedulerEntry? left, SchedulerEntry? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
            && left.Id == right.Id && left.Title == right.Title
            && left.Enabled == right.Enabled && SchedulerExecutionDefinition.Same(left, right);

    private SchedulerSettings RefreshConfiguration()
    {
        SchedulerSettings settings = Current();
        if (!SameConfiguration(settings))
        {
            var plan = new SchedulerCalendar(settings);
            Cancel(queue.Reconcile(settings));
            // 取消で保存した状態を、その後の消費追加で巻き戻さない。
            settings = Current();
            calendar = plan;
            applied = settings;
            ConfigureWindows(settings);
            errors.Clear();
            if (!settings.Enabled)
            {
                minutes.Stop();
            }
        }

        return settings;
    }

    private void RequireOwner()
    {
        if (ownershipLost || !ownsMonitor())
        {
            ownershipLost = true;
            throw new InvalidOperationException("予約監視の所有権がありません。");
        }
    }

    private void Publish(SchedulerExecutionRequest request)
    {
        if (!queue.TryEnqueue(request))
        {
            throw new InvalidOperationException("予約の待機受付が競合しました。");
        }
    }

    private void SaveStates(IEnumerable<SchedulerOneShotState> states)
    {
        RequireOwner();
        controller.Save(Current().WithOneShotStates(states));
        RequireOwner();
    }

    private void Transition(Guid occurrenceId, SchedulerOneShotStatus status)
    {
        SchedulerSettings settings = Current();
        SchedulerOneShotState? target = settings.OneShotStates.FirstOrDefault(
            state => state.OccurrenceId == occurrenceId);
        if (target is not null)
        {
            SaveStates(settings.OneShotStates.Select(state => ReferenceEquals(state, target)
                ? state.WithStatus(status) : state));
        }
    }

    private void Cancel(IReadOnlyList<SchedulerExecutionRequest> cancelled)
    {
        var ids = new HashSet<Guid>(cancelled.Select(request => request.OccurrenceId));
        SchedulerSettings settings = Current();
        if (settings.OneShotStates.Any(state => ids.Contains(state.OccurrenceId)
            && state.Status == SchedulerOneShotStatus.Accepted))
        {
            SaveStates(settings.OneShotStates.Select(state => ids.Contains(state.OccurrenceId)
                && state.Status == SchedulerOneShotStatus.Accepted
                    ? state.WithStatus(SchedulerOneShotStatus.Cancelled) : state));
        }
    }

    private void Fail(Exception error)
    {
        LastError = error;
        minutes.Stop();
        configurationEpoch = Guid.Empty;
        IReadOnlyList<SchedulerExecutionRequest> cancelled = queue.CancelAll();
        if (!ownershipLost)
        {
            try
            {
                RequireOwner();
                Cancel(cancelled);
            }
            catch (Exception secondary) when (Operational(secondary))
            {
                error.Data["予約取消状態の保存エラー"] = secondary.Message;
            }
        }
    }

    private static bool Operational(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException;
}
