using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace UWSCSharp.Wpf;

/// <summary>
/// 旧workerが残る失権状態で後継を開始し、回収前の受付と古い保存を防ぐことを検査します。
/// </summary>
internal static class SchedulerOverlappingHandoffVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt, bool owner)
    {
        string settings = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
        var clock = new VerificationClock();
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        var checks = new List<string>();
        var held = new List<Process>();
        Action? release = null;
        int preparations = 0;
        var host = new SchedulerRuntimeHost(Dispatcher.CurrentDispatcher,
            new SchedulerSettingsController(settings), new SchedulerApplicationSession(), gate,
            (_, _, _) => Task.FromException<SchedulerExecutionResult>(new NotSupportedException()),
            Environment.ProcessPath!, logs, Path.Combine(directory, "monitor"), clock, () => TimeZoneInfo.Utc,
            preparationCheck: _ =>
            {
                preparations++;
                if (!owner)
                {
                    Require(held.Count == 3 && held.All(process => process.HasExited),
                        "all-old-processes-exited-before-successor-runtime-start");
                }
                return Task.CompletedTask;
            }, diagnostics: owner ? new(MonitorOwned: close => release = close) : null);
        try
        {
            if (owner)
            {
                DateTimeOffset due = clock.Now.AddMinutes(1);
                string script = Path.Combine(directory, "old.uws");
                string probe = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
                string command = $"\"{probe}\" --scheduler-job-probe spawn \"{Path.Combine(directory, "descendants.json")}\" \"{Path.Combine(directory, "unused-release")}\"";
                string expression = string.Join("+CHR(34)+", command.Split('"').Select(part => "\"" + part + "\""));
                File.WriteAllText(script, "DIM f=FOPEN(\"old.ready\",F_WRITE8)\n"
                    + "FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))\nFCLOSE(f)\n"
                    + $"EXEC({expression},FALSE)\nSLEEP(120)\n", new UTF8Encoding(true));
                SchedulerSettingsStore.Save(settings, new SchedulerSettings([Entry(script, due)], enabled: true), null);
                Require(await host.StartAsync(), "old-owner-started");
                clock.Now = due;
                host.Poll();
                await WaitFor(() => File.Exists(Path.Combine(directory, "descendants.json"))
                    && host.Executions.Count == 1 && host.Executions[0].Timeline.StartedAt is not null);
                using JsonDocument data = await Read(Path.Combine(directory, "descendants.json"));
                int worker = int.Parse(File.ReadAllText(Path.Combine(directory, "old.ready")).Trim(), CultureInfo.InvariantCulture);
                int child = data.RootElement.GetProperty("pid").GetInt32();
                int grandchild = data.RootElement.GetProperty("child_pid").GetInt32();
                foreach (int pid in new[] { worker, child, grandchild })
                {
                    Hold(pid);
                }
                Require(held.All(process => !process.HasExited)
                    && held.All(process => host.OwnedProcessIds.Contains((uint)process.Id)),
                    "three-live-old-processes-owned-before-loss");
                SchedulerRuntimeHost.Execution execution = host.Executions.Single();
                Publish(receipt, new
                {
                    result = "Ready",
                    process_id = Environment.ProcessId,
                    worker,
                    child,
                    grandchild,
                    due,
                    occurrence = execution.Dispatch.Request.OccurrenceId
                });
                await WaitFor(() => File.Exists(Path.Combine(directory, "release-owner")));
                release!();
                Require(!host.OwnsMonitor && held.All(process => !process.HasExited),
                    "lease-released-with-three-old-processes-still-live");
                Publish(receipt + ".paused", new
                {
                    result = "Paused"
                });
                // 専用ゲスト内の診断だけで旧本体の処理を止め、後継の実Job回収を先行させます。
                var limit = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "resume-old")))
                {
                    if (limit.Elapsed > TimeSpan.FromSeconds(60))
                    {
                        throw new TimeoutException("旧本体の再開通知がありません。");
                    }
                    Thread.Sleep(25);
                }
                host.Poll();
                await WaitFor(() => execution.Finished.Task.IsCompleted);
                Require(held.All(process => process.HasExited)
                    && execution.Result is
                    {
                        Outcome: SchedulerExecutionOutcome.Unknown,
                        ResourcesRecovered: false, Started: true
                    } && !execution.ResultSaved
                    && !host.OwnsMonitor, "old-completion-after-takeover-cannot-save-or-reacquire");
                SchedulerSettings loaded = SchedulerSettingsStore.Load(settings).Settings;
                Require(loaded.OneShotStates.Single(state => state.OccurrenceId
                    == execution.Dispatch.Request.OccurrenceId).Status == SchedulerOneShotStatus.Unknown
                    && loaded.OneShotStates.Any(state => state.Status == SchedulerOneShotStatus.Succeeded),
                    "old-completion-does-not-overwrite-successor-results");
                Require(!await host.StopAsync(TimeSpan.FromSeconds(20)) && !host.CanClose
                    && gate.ScheduledCount == 1 && host.OwnedProcessIds.Count == 0,
                    "unacknowledged-old-input-keeps-slot-after-process-recovery");
            }
            else
            {
                using JsonDocument original = await Read(Path.Combine(directory, "owner.json"));
                clock.Now = original.RootElement.GetProperty("due").GetDateTimeOffset();
                Guid occurrence = original.RootElement.GetProperty("occurrence").GetGuid();
                foreach (string name in new[] { "worker", "child", "grandchild" })
                {
                    Hold(original.RootElement.GetProperty(name).GetInt32());
                }
                Require(held.All(process => !process.HasExited) && !await host.StartAsync()
                    && preparations == 0, "successor-busy-while-old-owner-holds-lease");
                Publish(receipt + ".busy", new
                {
                    result = "Busy"
                });
                await WaitFor(() => File.Exists(Path.Combine(directory, "takeover")));
                Require(held.All(process => !process.HasExited), "successor-starts-with-three-old-processes-live");
                Require(await host.StartAsync() && preparations == 1 && host.Executions.Count == 0,
                    "successor-prepares-before-accepting-any-run");
                SchedulerSettingsSnapshot snapshot = SchedulerSettingsStore.Load(settings);
                Require(snapshot.Settings.OneShotStates.Single().OccurrenceId == occurrence
                    && snapshot.Settings.OneShotStates.Single().Status == SchedulerOneShotStatus.Unknown,
                    "successor-consumes-old-occurrence-as-unknown-without-replay");
                DateTimeOffset due = clock.Now.AddMinutes(1);
                string next = Path.Combine(directory, "next.uws");
                File.WriteAllText(next, "PRINT \"successor-run\"\n", new UTF8Encoding(true));
                var editor = new SchedulerSettingsController(settings);
                editor.Reload();
                editor.Save(snapshot.Settings.WithSlot(1, Entry(next, due)));
                clock.Now = due;
                host.Poll();
                await WaitFor(() => host.Executions.Count == 1 && host.ActiveCount == 0);
                Require(host.Executions[0].Result is
                {
                    Outcome: SchedulerExecutionOutcome.Succeeded,
                    ResourcesRecovered: true, ExitCode: 0
                } && host.Executions[0].ResultSaved,
                    "new-successor-run-succeeds-after-old-job-drain");
                Require(await host.StopAsync(TimeSpan.FromSeconds(20)) && host.CanClose,
                    "successor-releases-its-monitor-after-cleanup");
            }
            Publish(receipt, new
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
            if (!owner && host.CanClose)
            {
                host.Dispose();
            }
            foreach (Process process in held)
            {
                process.Dispose();
            }
            // 旧本体の未回収枠は偽って解放せず、専用診断の終了でOS資源を閉じます。
        }

        SchedulerEntry Entry(string path, DateTimeOffset due) => new(Guid.NewGuid(), Path.GetFileName(path),
            path, [], SchedulerExecutionMode.SeparateProcess,
            new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime),
                TimeOnly.FromDateTime(due.UtcDateTime)), directory);

        void Hold(int pid)
        {
            var process = Process.GetProcessById(pid);
            held.Add(process);
            _ = process.Handle;
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

    private static void Publish(string path, object value)
    {
        string prepared = path + ".prepared";
        File.WriteAllText(prepared, JsonSerializer.Serialize(value));
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
