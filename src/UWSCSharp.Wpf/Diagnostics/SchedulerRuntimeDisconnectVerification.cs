using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace UWSCSharp.Wpf;

/// <summary>
/// 通常の本体とworkerの制御切断を、予約・履歴・実行枠まで通して検査します。
/// </summary>
internal static class SchedulerRuntimeDisconnectVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        string settings = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
        string lease = Path.Combine(directory, "monitor");
        var clock = new VerificationClock();
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        DateTimeOffset due = clock.Now.AddMinutes(1);
        SchedulerEntry broken = Entry("broken", due);
        SchedulerEntry healthy = Entry("healthy", due);
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([broken, healthy], enabled: true), null);
        var connections = new Dictionary<Guid, Action>();
        var checks = new List<string>();
        // 未回収枠を保持することが検証対象のため、診断の終了までホストを保持します。
        var host = new SchedulerRuntimeHost(Dispatcher.CurrentDispatcher,
            new SchedulerSettingsController(settings), new SchedulerApplicationSession(), gate,
            (_, _, _) => Task.FromException<SchedulerExecutionResult>(new NotSupportedException()),
            Environment.ProcessPath!, logs, lease, clock, () => TimeZoneInfo.Utc,
            diagnostics: new(WorkerControlConnected:
                (dispatch, close) => connections.Add(dispatch.Request.Entry.Id, close)));
        try
        {
            Require(await host.StartAsync(), "runtime-monitor-started");
            clock.Now = due;
            host.Poll();
            await WaitFor(() => new[] { broken, healthy }.All(entry =>
                File.Exists(Path.ChangeExtension(entry.Path, ".ready")))
                && host.Executions.Count == 2
                && host.Executions.All(execution => execution.Timeline.StartedAt is not null));
            SchedulerRuntimeHost.Execution faulted = host.Executions.Single(
                execution => execution.Dispatch.Request.Entry.Id == broken.Id);
            SchedulerRuntimeHost.Execution parallel = host.Executions.Single(
                execution => execution.Dispatch.Request.Entry.Id == healthy.Id);
            Require(connections.Count == 2 && gate.ScheduledCount == 2 && gate.CanEnterInteractive,
                "two-normal-workers-started-with-independent-connections");
            int pid = int.Parse(File.ReadAllText(Path.ChangeExtension(broken.Path, ".ready")).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.GetProcessById(pid);
            _ = process.Handle;
            Require(!process.HasExited && host.OwnedProcessIds.Contains((uint)pid),
                "actual-worker-live-before-control-close");
            connections[broken.Id]();
            await WaitFor(() => faulted.Finished.Task.IsCompleted);
            Require(process.HasExited && faulted.Result is
            {
                Outcome: SchedulerExecutionOutcome.Unknown, Started: true, Forced: true,
                ResourcesRecovered: false, ExitCode: not null
            } && faulted.ResultSaved,
                "normal-client-persists-started-unknown-without-resource-acknowledgement");
            Require(host.ActiveCount == 2 && gate.ScheduledCount == 2
                && !gate.CanEnterInteractive && !parallel.Finished.Task.IsCompleted,
                "unknown-keeps-its-slot-and-blocks-interactive-without-stopping-peer");
            SchedulerOneShotState state = SchedulerSettingsStore.Load(settings).Settings.OneShotStates
                .Single(value => value.EntryId == broken.Id);
            Require(state.Status == SchedulerOneShotStatus.Unknown
                && state.OccurrenceId == faulted.Dispatch.Request.OccurrenceId,
                "one-shot-remains-consumed-as-unknown");
            File.WriteAllText(Path.Combine(directory, "healthy.go"), "go");
            await WaitFor(() => parallel.Finished.Task.IsCompleted && host.ActiveCount == 1);
            Require(parallel.Result is
            {
                Outcome: SchedulerExecutionOutcome.Succeeded,
                ResourcesRecovered: true, ExitCode: 0
            } && parallel.ResultSaved
                && gate.ScheduledCount == 1, "parallel-worker-completes-and-releases-only-its-slot");
            var editor = new SchedulerSettingsController(settings);
            editor.Reload();
            DateTimeOffset nextDue = clock.Now.AddMinutes(1);
            SchedulerEntry next = Entry("next", nextDue);
            editor.Save(editor.Current!.Settings.WithSlot(2, next));
            clock.Now = nextDue;
            host.Poll();
            Require(host.Executions.Count == 2 && connections.Count == 2
                && !File.Exists(Path.ChangeExtension(next.Path, ".ready"))
                && host.OwnedProcessIds.Count == 0 && !gate.CanEnterInteractive,
                "unacknowledged-input-blocks-new-dispatch-without-replaying-old-one-shot");
            Require(!await host.StopAsync(TimeSpan.FromSeconds(20)) && !host.CanClose
                && host.OwnsMonitor && !host.IsActive && gate.ScheduledCount == 1,
                "stop-retains-monitor-and-refuses-close-with-unrecovered-slot");
            var storage = new SchedulerManagedStorage(logs);
            SchedulerHistoryRecord history = storage.ReadHistoryRecord(faulted.Dispatch.Request.OccurrenceId);
            Require(history.Outcome == SchedulerExecutionOutcome.Unknown && history.Started
                && history.Forced && !history.ResourcesRecovered && history.IsProtected
                && history.StartedAt is not null && history.EndedAt is not null
                && history.ResultObservedAt is not null,
                "persisted-history-keeps-unknown-and-protection-with-observed-end");
            Require(storage.ReadHistoryRecord(parallel.Dispatch.Request.OccurrenceId).Outcome
                == SchedulerExecutionOutcome.Succeeded, "parallel-history-remains-successful");
            using (var contender = SchedulerMonitorLease.TryAcquire(lease))
            {
                Require(contender is null, "successor-cannot-acquire-unrecovered-owner");
            }
            bool refused = false;
            try
            {
                host.Dispose();
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            Require(refused && host.OwnsMonitor, "dispose-cannot-bypass-unrecovered-slot");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                worker_pid = pid,
                history.OccurrenceId,
                history.ExitCode,
                history.ResourcesRecovered,
                checks
            }));
            return 0;
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(20));
            // 回収を偽って解放せず、専用診断プロセスの終了でOS資源を閉じます。
        }

        SchedulerEntry Entry(string name, DateTimeOffset when)
        {
            string path = Path.Combine(directory, name + ".uws");
            File.WriteAllText(path, $"DIM f=FOPEN(\"{name}.ready\",F_WRITE8)\n"
                + "FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))\nFCLOSE(f)\n"
                + $"WHILE !FOPEN(\"{name}.go\",F_EXISTS)\nSLEEP(0.02)\nWEND\n", new UTF8Encoding(true));
            return new SchedulerEntry(Guid.NewGuid(), name, path, [], SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(when.UtcDateTime),
                    TimeOnly.FromDateTime(when.UtcDateTime)), directory);
        }

        async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!condition())
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private sealed class VerificationClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
