using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace UWSCSharp.Wpf;

/// <summary>
/// 生きた旧本体の監視ハンドル喪失と、別本体への引継ぎを検査します。
/// </summary>
internal static class SchedulerOwnershipLossVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt, bool owner)
    {
        string settings = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
        var clock = new VerificationClock();
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        var checks = new List<string>();
        Action? loseOwnership = null;
        int preparations = 0;
        using var host = new SchedulerRuntimeHost(Dispatcher.CurrentDispatcher,
            new SchedulerSettingsController(settings), new SchedulerApplicationSession(), gate,
            (_, _, _) => Task.FromException<SchedulerExecutionResult>(new NotSupportedException()),
            Environment.ProcessPath!, logs, Path.Combine(directory, "monitor"), clock, () => TimeZoneInfo.Utc,
            preparationCheck: _ =>
            {
                preparations++;
                return Task.CompletedTask;
            }, diagnostics: owner ? new(MonitorOwned: close => loseOwnership = close) : null);
        try
        {
            if (owner)
            {
                DateTimeOffset due = clock.Now.AddMinutes(1);
                SchedulerEntry old = Entry("old", due, wait: true);
                SchedulerSettingsStore.Save(settings, new SchedulerSettings([old], enabled: true), null);
                Require(await host.StartAsync(), "owner-monitor-started");
                clock.Now = due;
                host.Poll();
                await WaitFor(() => File.Exists(Path.Combine(directory, "old.ready"))
                    && host.Executions.Count == 1 && host.Executions[0].Timeline.StartedAt is not null);
                SchedulerRuntimeHost.Execution execution = host.Executions[0];
                int pid = int.Parse(File.ReadAllText(Path.Combine(directory, "old.ready")).Trim(), CultureInfo.InvariantCulture);
                using var worker = Process.GetProcessById(pid);
                _ = worker.Handle;
                Require(!worker.HasExited && host.OwnsMonitor && loseOwnership is not null,
                    "owner-and-worker-alive-before-real-lease-close");
                await Publish(receipt, new
                {
                    result = "Ready",
                    process_id = Environment.ProcessId,
                    worker_pid = pid,
                    due,
                    occurrence = execution.Dispatch.Request.OccurrenceId
                });
                await WaitFor(() => File.Exists(Path.Combine(directory, "lose-lease")));
                loseOwnership!();
                Require(!host.OwnsMonitor && !worker.HasExited,
                    "real-monitor-handle-closed-while-old-processes-alive");
                host.Poll();
                await WaitFor(() => execution.Finished.Task.IsCompleted && host.ActiveCount == 0);
                Require(worker.HasExited && execution.Result is
                {
                    Outcome: SchedulerExecutionOutcome.Cancelled,
                    ResourcesRecovered: true, Started: true
                } && !execution.ResultSaved
                    && !host.IsActive && gate.ScheduledCount == 0,
                    "old-host-cancels-worker-and-cannot-save-result-after-ownership-loss");
                Require(SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status
                    == SchedulerOneShotStatus.Started, "old-owner-does-not-rewrite-consumed-state");
                Require(await host.StopAsync(TimeSpan.FromSeconds(20)) && host.CanClose,
                    "old-host-stops-after-cooperative-recovery");
                await Publish(receipt + ".lost", new
                {
                    result = "Lost",
                    checks
                });
                await WaitFor(() => File.Exists(Path.Combine(directory, "finish")));
                host.Poll();
                Require(!host.OwnsMonitor && host.Executions.Count == 1
                    && SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single(
                        state => state.OccurrenceId == execution.Dispatch.Request.OccurrenceId).Status
                        == SchedulerOneShotStatus.Unknown,
                    "old-live-host-does-not-reacquire-or-overwrite-successor-unknown");
            }
            else
            {
                using JsonDocument original = await Read(Path.Combine(directory, "owner.json"));
                clock.Now = original.RootElement.GetProperty("due").GetDateTimeOffset();
                Guid occurrence = original.RootElement.GetProperty("occurrence").GetGuid();
                Require(!await host.StartAsync() && preparations == 0 && host.Executions.Count == 0,
                    "successor-refuses-live-owner-before-loss");
                await Publish(receipt + ".busy", new
                {
                    result = "Busy"
                });
                await WaitFor(() => File.Exists(Path.Combine(directory, "takeover")));
                Require(await host.StartAsync() && preparations == 1,
                    "successor-prepares-after-live-owner-releases-lease");
                SchedulerSettingsSnapshot snapshot = SchedulerSettingsStore.Load(settings);
                Require(snapshot.Settings.OneShotStates.Single().OccurrenceId == occurrence
                    && snapshot.Settings.OneShotStates.Single().Status == SchedulerOneShotStatus.Unknown
                    && host.Executions.Count == 0, "successor-restores-unknown-without-replay");
                DateTimeOffset nextDue = clock.Now.AddMinutes(1);
                SchedulerEntry next = Entry("next", nextDue, wait: false);
                var editor = new SchedulerSettingsController(settings);
                editor.Reload();
                editor.Save(snapshot.Settings.WithSlot(1, next));
                clock.Now = nextDue;
                host.Poll();
                await WaitFor(() => host.Executions.Count == 1 && host.ActiveCount == 0);
                Require(host.Executions[0].Result is
                {
                    Outcome: SchedulerExecutionOutcome.Succeeded,
                    ResourcesRecovered: true, ExitCode: 0
                } && host.Executions[0].ResultSaved,
                    "successor-executes-new-one-shot-normally");
                Require(SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single(
                    state => state.OccurrenceId == occurrence).Status == SchedulerOneShotStatus.Unknown,
                    "new-result-keeps-old-unknown");
                Require(await host.StopAsync(TimeSpan.FromSeconds(20)) && host.CanClose,
                    "successor-releases-monitor-after-cleanup");
            }
            await Publish(receipt, new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                checks
            });
            return 0;
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(20));
        }

        SchedulerEntry Entry(string name, DateTimeOffset due, bool wait)
        {
            string path = Path.Combine(directory, name + ".uws");
            File.WriteAllText(path, $"DIM f=FOPEN(\"{name}.ready\",F_WRITE8)\n"
                + "FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))\nFCLOSE(f)\n"
                + (wait ? "SLEEP(120)\n" : "PRINT \"successor\"\n"), new UTF8Encoding(true));
            return new SchedulerEntry(Guid.NewGuid(), name, path, [], SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime),
                    TimeOnly.FromDateTime(due.UtcDateTime)), directory);
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

    private static async Task Publish(string path, object value)
    {
        string prepared = path + ".prepared";
        await File.WriteAllTextAsync(prepared, JsonSerializer.Serialize(value));
        File.Move(prepared, path, overwrite: true);
    }

    private static async Task<JsonDocument> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed class VerificationClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
