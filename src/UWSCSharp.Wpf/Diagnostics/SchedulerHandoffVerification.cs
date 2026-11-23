using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerHandoffVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt, bool owner)
    {
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
        string ownerReceipt = Path.Combine(directory, "owner.json");
        var clock = new SchedulerVerificationClock();
        var checks = new List<string>();
        var heldProcesses = new List<Process>();
        int preparationChecks = 0;
        var main = new MainWindow(false, launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        using SchedulerRuntimeHost host = access.CreatePlaybackHost(
            settingsPath, logs, Path.Combine(directory, "monitor"),
            clock, () => TimeZoneInfo.Utc,
            preparationCheck: _ =>
            {
                preparationChecks++;
                if (!owner)
                {
                    Require(heldProcesses.Count == 3 && heldProcesses.All(process => process.HasExited),
                        "old-worker-and-descendants-exited-before-runtime-start");
                }
                return Task.CompletedTask;
            });
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            if (owner)
            {
                string script = Path.Combine(directory, "old.uws");
                string probe = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
                string command = $"\"{probe}\" --scheduler-job-probe spawn \"{Path.Combine(directory, "descendant.json")}\" \"{Path.Combine(directory, "release")}\"";
                string expression = string.Join("+CHR(34)+", command.Split('"').Select(part => "\"" + part + "\""));
                WriteRuntimeHostScript(script, "DIM f=FOPEN(\"worker.ready\",F_WRITE8)\n"
                    + "FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))\nFCLOSE(f)\n"
                    + $"EXEC({expression},FALSE)\nSLEEP(120)\n");
                DateTimeOffset due = clock.Now.AddMinutes(1);
                var entry = new SchedulerEntry(Guid.NewGuid(), "old", script, [], SchedulerExecutionMode.SeparateProcess,
                    new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime), TimeOnly.FromDateTime(due.UtcDateTime)), directory);
                SchedulerSettingsStore.Save(settingsPath, new SchedulerSettings([entry], enabled: true), null);
                Require(await host.StartAsync(), "owner-monitor-started");
                clock.Now = due;
                host.Poll();
                await WaitForRuntimeHost(() => File.Exists(Path.Combine(directory, "worker.ready"))
                    && File.Exists(Path.Combine(directory, "descendant.json")));
                using JsonDocument descendants = await ReadHandoffReceipt(Path.Combine(directory, "descendant.json"));
                int worker = int.Parse(File.ReadAllText(Path.Combine(directory, "worker.ready")).Trim(), CultureInfo.InvariantCulture);
                int child = descendants.RootElement.GetProperty("pid").GetInt32();
                int grandchild = descendants.RootElement.GetProperty("child_pid").GetInt32();
                uint[] members = [.. host.OwnedProcessIds];
                Require(new[] { worker, child, grandchild }.All(pid => members.Contains((uint)pid)),
                    "actual-worker-and-two-descendants-belong-to-owner-job");
                SchedulerOneShotState state = SchedulerSettingsStore.Load(settingsPath).Settings.OneShotStates.Single();
                Require(host.ActiveCount == 1 && state.Status is SchedulerOneShotStatus.Accepted or SchedulerOneShotStatus.Started,
                    "one-shot-consumed-before-owner-termination");
                await Publish(new
                {
                    result = "Ready",
                    process_id = Environment.ProcessId,
                    clock = clock.Now,
                    worker,
                    child,
                    grandchild,
                    occurrence = state.OccurrenceId,
                    checks
                });
                await WaitForRuntimeHost(() => File.Exists(Path.Combine(directory, "owner-normal-stop")));
                return 0;
            }

            using JsonDocument old = await ReadHandoffReceipt(ownerReceipt);
            JsonElement original = old.RootElement;
            clock.Now = original.GetProperty("clock").GetDateTimeOffset();
            Guid occurrence = original.GetProperty("occurrence").GetGuid();
            foreach (string key in new[] { "worker", "child", "grandchild" })
            {
                var member = Process.GetProcessById(original.GetProperty(key).GetInt32());
                _ = member.Handle;
                if (member.HasExited)
                {
                    member.Dispose();
                    throw new InvalidOperationException("引継ぎ開始前に旧プロセスが終了しました。");
                }
                heldProcesses.Add(member);
            }
            Require(!await host.StartAsync() && preparationChecks == 0 && host.Executions.Count == 0,
                "successor-refuses-busy-owner-without-preparation-or-dispatch");
            await File.WriteAllTextAsync(receipt + ".busy", "busy");
            await WaitForRuntimeHost(() => File.Exists(Path.Combine(directory, "takeover")));
            int startAttempts = 1;
            var acquireWait = Stopwatch.StartNew();
            while (!await host.StartAsync())
            {
                // 旧本体の終了後も監視ファイルを取得できない間だけ再試行します。
                if (host.LastError is not null || preparationChecks != 0
                    || acquireWait.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    throw new InvalidOperationException(
                        "後継の予約監視を開始できませんでした。"
                        + $" 試行回数={startAttempts}、準備回数={preparationChecks}。",
                        host.LastError);
                }

                await Task.Delay(25);
                startAttempts++;
            }

            Require(preparationChecks == 1, "successor-prepared-before-acceptance");
            SchedulerSettingsSnapshot snapshot = SchedulerSettingsStore.Load(settingsPath);
            SchedulerOneShotState previous = snapshot.Settings.OneShotStates.Single();
            Require(previous.OccurrenceId == occurrence && previous.Status == SchedulerOneShotStatus.Unknown
                && host.Executions.Count == 0, "previous-one-shot-retained-as-unknown-without-replay");
            string nextScript = Path.Combine(directory, "next.uws");
            WriteRuntimeHostScript(nextScript, "DIM f=FOPEN(\"next.ready\",F_WRITE8)\nFCLOSE(f)\nPRINT \"new-run\"\n");
            DateTimeOffset nextDue = clock.Now.AddMinutes(1);
            var next = new SchedulerEntry(Guid.NewGuid(), "next", nextScript, [], SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(nextDue.UtcDateTime), TimeOnly.FromDateTime(nextDue.UtcDateTime)), directory);
            var editor = new SchedulerSettingsController(settingsPath);
            editor.Reload();
            editor.Save(snapshot.Settings.WithSlot(1, next));
            clock.Now = nextDue;
            host.Poll();
            await WaitForRuntimeHost(() => host.Executions.Count == 1 && host.ActiveCount == 0);
            Require(host.Executions[0].Dispatch.Request.Entry.Id == next.Id
                && host.Executions[0].Result is { ExitCode: 0, ResourcesRecovered: true }
                && File.Exists(Path.Combine(directory, "next.ready")), "new-run-succeeds-after-old-generation-recovery");
            Require(SchedulerSettingsStore.Load(settingsPath).Settings.OneShotStates
                .Single(state => state.OccurrenceId == occurrence).Status == SchedulerOneShotStatus.Unknown,
                "new-completion-does-not-rewrite-old-unknown-result");
            Require(await host.StopAsync(TimeSpan.FromSeconds(20)) && host.CanClose, "successor-stops-and-releases-owner");
            await Publish(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                start_attempts = startAttempts,
                checks
            });
            return 0;
        }
        finally
        {
            if (!await host.StopAsync(TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("引継ぎ検証の資源回収を確認できません。");
            }
            foreach (Process process in heldProcesses)
            {
                process.Dispose();
            }
            main.Close();
        }

        async Task Publish(object value)
        {
            string prepared = receipt + ".prepared";
            await File.WriteAllTextAsync(prepared, JsonSerializer.Serialize(value));
            File.Move(prepared, receipt, overwrite: true);
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private static async Task<JsonDocument> ReadHandoffReceipt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }
}
