using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerRuntimeHostVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "schedule.json");
        string leasePath = Path.Combine(directory, "schedule.monitor");
        string logs = Path.Combine(directory, "logs");
        var clock = new SchedulerVerificationClock();
        var application = new SchedulerApplicationSession();
        var checks = new List<string>();
        var errors = new List<Exception>();
        int keyboardScopes = 0;
        int keyboardRestored = 0;
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add, launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        SchedulerRuntimeHost CreateHost(string executable) => new(main.Dispatcher,
            new SchedulerSettingsController(settingsPath), application, access.Gate,
            async (dispatch, token, started) =>
                (await main.RunScheduledPlaybackAsync(dispatch, logs, token, started)).Result,
            executable, logs, leasePath, clock, () => TimeZoneInfo.Utc,
            async (key, operation, token) =>
            {
                keyboardScopes++;
                await access.RunWorkerKeyboardAsync(key, async () =>
                {
                    Require(!access.IsParentHotkeyRegistered(4), "worker-key-parent-registration-suspended");
                    await operation();
                }, token);
                keyboardRestored++;
            });
        using SchedulerRuntimeHost host = CreateHost(Environment.ProcessPath!);
        SchedulerRuntimeHost? bad = null;
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            if (!access.ApplyParentHotkeys(new HotkeySettings(Modifiers: 0)))
            {
                throw new InvalidOperationException("検証用の親ホットキーを登録できません。");
            }
            access.SetDraft("未保存の原稿", "編集出力");
            SchedulerEntry[] initial = [.. new[] { "same", "a", "b", "c", "waiting" }.Select(key =>
            {
                string path = Path.Combine(directory, key + ".uws");
                WriteRuntimeHostScript(path, $"DIM f=FOPEN(\"{key}.ready\",F_WRITE8)\n"
                    + "FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))\nFCLOSE(f)\n"
                    + $"WHILE !FOPEN(\"{key}.go\",F_EXISTS)\nSLEEP(0.02)\nWEND\nPRINT \"{key}\"");
                return Entry(path, key == "same" ? SchedulerExecutionMode.SameProcess
                    : SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition());
            })];
            SchedulerSettingsStore.Save(settingsPath, new SchedulerSettings(initial, enabled: true), null);
            Require(await host.StartAsync(), "host-start");
            await WaitForRuntimeHost(() => initial.Take(4).All(entry => File.Exists(
                Path.ChangeExtension(entry.Path, ".ready"))));
            Require(host.ActiveCount == 4, "real-four-ready");
            await WaitForRuntimeHost(() => host.Executions.All(value => value.Timeline.StartedAt is not null));
            Require(host.Executions.All(value => value.Timeline.StartedAt is not null
                && value.Timeline.EndedAt is null), "observed-start-for-both-modes-before-completion");
            Require(!File.Exists(Path.Combine(directory, "waiting.ready")),
                "four-slots-queued-fifth-not-started");
            int[] pids = [.. initial.Take(4).Select(entry => int.Parse(
                File.ReadAllText(Path.ChangeExtension(entry.Path, ".ready")).Trim(),
                CultureInfo.InvariantCulture))];
            Require(pids[0] == Environment.ProcessId && pids.Skip(1).All(pid => pid != pids[0])
                && pids.Distinct().Count() == 4, "same-and-separate-pids");
            using (SchedulerRuntimeHost contender = CreateHost(Environment.ProcessPath!))
            {
                Require(!await contender.StartAsync() && !contender.OwnsMonitor
                    && SchedulerSettingsStore.Load(settingsPath).Settings.Enabled,
                    "nonowner-no-start-no-settings-disable");
            }
            var editor = new SchedulerSettingsController(settingsPath);
            editor.Reload();
            editor.Save(editor.Current!.Settings.WithOptions(false, false, false));
            host.Poll();
            Require(!host.IsActive && host.ActiveCount == 4 && host.OwnsMonitor,
                "disabled-keeps-four-running");
            SchedulerRuntimeHost.Execution cancelled = host.Executions.Single(
                execution => execution.Dispatch.Request.Entry.Id == initial[1].Id);
            Require(host.Cancel(cancelled.Dispatch.Request.OccurrenceId), "cancel-specific-occurrence");
            await WaitForRuntimeHost(() => host.ActiveCount == 3);
            Require(cancelled.Result?.Outcome == SchedulerExecutionOutcome.Cancelled
                && host.Executions.Count(execution => execution.Result is null) == 3,
                "cancel-does-not-stop-other-three");
            SchedulerRuntimeHost.Execution same = host.Executions.Single(
                execution => execution.Dispatch.Request.Entry.Id == initial[0].Id);
            Require(host.Cancel(same.Dispatch.Request.OccurrenceId), "cancel-same-occurrence");
            await WaitForRuntimeHost(() => host.ActiveCount == 2);
            Require(same.Result?.Outcome == SchedulerExecutionOutcome.Cancelled
                && host.Executions.Count(execution => execution.Result is null) == 2,
                "same-cancel-does-not-stop-separate-workers");
            Require(!File.Exists(Path.Combine(directory, "waiting.ready")),
                "disabled-cancels-waiting-fifth");
            foreach (string key in new[] { "b", "c" })
            {
                File.WriteAllText(Path.Combine(directory, key + ".go"), "go");
            }
            await WaitForRuntimeHost(() => host.ActiveCount == 0 && !host.OwnsMonitor);
            Require(host.Executions.Count == 4 && host.Executions.All(execution =>
                execution.ResultSaved && execution.Result?.ResourcesRecovered == true),
                "all-first-resources-recovered-owner-released");
            Require(EditorPreserved(), "editor-state-preserved");

            editor.Reload();
            editor.Save(editor.Current!.Settings.WithOptions(true, false, false));
            Require(await host.StartAsync() && host.ActiveCount == 0
                && !File.Exists(Path.Combine(directory, "waiting.ready")),
                "reenable-without-startup-repeat");
            host.StopAccepting();
            SchedulerEntry once = Once("once", 1, "KBD(118,CLICK)\nPRINT \"once\"");
            Save(once, wakeUp: true);
            if (!await host.StartAsync())
            {
                throw new InvalidOperationException("一回条件の監視を開始できません。");
            }
            await WaitForRuntimeHost(() => host.WakeError is not null || host.WakeTime is not null);
            Require(host.IsActive && (host.WakeError is NotSupportedException
                || host.WakeTime == clock.Now.AddMinutes(1)), "wake-request-owned-and-unsupported-is-nonfatal");
            clock.Now = clock.Now.AddMinutes(1);
            await WaitForRuntimeHost(() => host.Executions.Count == 5 && host.ActiveCount == 0);
            Require(SavedState() == SchedulerOneShotStatus.Succeeded,
                "timer-runs-one-shot-and-persists-success");
            Require(keyboardScopes == 1 && keyboardRestored == 1
                && access.IsParentHotkeyRegistered(4) && EditorPreserved(),
                "worker-key-restored-before-one-shot-success");
            host.Poll();
            Require(host.Executions.Count == 5, "one-shot-not-repeated");
            host.StopAccepting();
            if (!await host.StopAsync(TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("復帰計算の取消と終了を確認できません。");
            }

            SchedulerEntry failure = Once("start-failure", 1, "PRINT \"must-not-run\"");
            Save(failure);
            bad = CreateHost(Path.Combine(directory, "missing-worker.exe"));
            Require(await bad.StartAsync(), "failure-monitor-start");
            clock.Now = clock.Now.AddMinutes(1);
            bad.Poll();
            await WaitForRuntimeHost(() => bad.ActiveCount == 0 && bad.Executions.Count == 1);
            Require(SavedState() == SchedulerOneShotStatus.Failed
                && bad.Executions[0].Result is { Started: false, ExitCode: null, ResourcesRecovered: true },
                "confirmed-worker-start-failure-persisted");
            Require(bad.Executions[0].Timeline.StartedAt is null
                && bad.Executions[0].Timeline.EndedAt is not null,
                "confirmed-start-failure-has-no-invented-start-time");
            bad.StopAccepting();
            bad.Dispose();
            bad = null;

            Save(Once("shutdown", 1, "DIM f=FOPEN(\"shutdown.ready\",F_WRITE8)\nFCLOSE(f)\nSLEEP(30)"));
            Require(await host.StartAsync(), "shutdown-monitor-start");
            clock.Now = clock.Now.AddMinutes(1);
            host.Poll();
            await WaitForRuntimeHost(() => File.Exists(Path.Combine(directory, "shutdown.ready")));
            Task<bool> stopping = host.StopAsync(TimeSpan.FromSeconds(20));
            bool rejected = false;
            try
            {
                await host.StartAsync();
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Require(rejected, "shutdown-cancel-and-restart-rejected");
            Require(await stopping && host.CanClose && host.ActiveCount == 0
                && SavedState() == SchedulerOneShotStatus.Failed,
                "shutdown-all-recovered");
            Require(EditorPreserved() && errors.Count == 0, "editor-after-cancel-preserved");
            Require(host.Executions.All(value => value.Timeline.StartedAt is not null
                && value.Timeline.EndedAt is not null
                && value.Timeline.AcceptedAt == value.Dispatch.Request.ObservedAt),
                "completion-times-retain-original-receipt-time");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                dispatches = 7,
                checks
            }), new UTF8Encoding(false));
            return 0;
        }
        finally
        {
            if (bad is not null)
            {
                await bad.StopAsync(TimeSpan.FromSeconds(20));
                bad.Dispose();
            }
            if (!await host.StopAsync(TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("実行ホストの回収を確認できません。");
            }
            access.DiscardDraft();
            main.Close();
        }

        bool EditorPreserved() => access.DraftText == "未保存の原稿"
            && access.OutputText == "編集出力" && access.IsModified;

        SchedulerEntry Entry(string path, SchedulerExecutionMode mode, SchedulerCondition condition) =>
            new(Guid.NewGuid(), Path.GetFileName(path), path, [], mode, condition, directory);

        SchedulerEntry Once(string key, int minutes, string script)
        {
            string path = Path.Combine(directory, key + ".uws");
            WriteRuntimeHostScript(path, script);
            DateTimeOffset due = clock.Now.AddMinutes(minutes);
            return Entry(path, SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime),
                    TimeOnly.FromDateTime(due.UtcDateTime)));
        }

        void Save(SchedulerEntry entry, bool wakeUp = false)
        {
            var editor = new SchedulerSettingsController(settingsPath);
            editor.Reload();
            editor.Save(new SchedulerSettings([entry], enabled: true, wakeUp: wakeUp));
        }

        SchedulerOneShotStatus SavedState() => SchedulerSettingsStore.Load(settingsPath)
            .Settings.OneShotStates.Single().Status;

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
