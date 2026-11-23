using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerWindowVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
        string title = "SchedulerWindow-" + Guid.NewGuid().ToString("N");
        var checks = new List<string>();
        int startSounds = 0;
        var main = new MainWindow(false, launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        var target = new Window { Title = title, ShowActivated = false, Width = 160, Height = 80 };
        var hosts = new List<SchedulerRuntimeHost>();
        TaskCompletionSource<IReadOnlyList<WindowsObservedWindow>>? delayedRelease = null;
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            target.Show();
            nint handle = new WindowInteropHelper(target).Handle;
            target.Hide();
            SchedulerEntry[] entries = [.. new[] { SchedulerExecutionMode.SameProcess,
                SchedulerExecutionMode.SeparateProcess }.Select(mode =>
            {
                string path = Path.Combine(directory, mode + ".uws");
                WriteRuntimeHostScript(path, "DIM w=GETID(GET_SCHEDULE_WIN)\n"
                    + $"DIM f=FOPEN(\"{mode}.txt\",F_WRITE8)\n"
                    + "FPUT(f,STATUS(w,ST_PROCESS))\nFCLOSE(f)\n");
                return new SchedulerEntry(Guid.NewGuid(), mode.ToString(), path, [], mode,
                    new SchedulerWindowCondition(title, ""), directory);
            })];
            Save(entries);
            var settings = new SchedulerSettingsController(settingsPath);
            settings.Reload();
            var draft = new SchedulerRegistrationDraft(settings.Current!);
            var registration = new SchedulerSettingsWindow(draft, directory, _ => { })
            {
                Owner = main,
                ShowActivated = false
            };
            try
            {
                registration.Show();
                CheckBox sound = FindRegistrationControl<CheckBox>(registration, "ScheduleStartSoundEnabled");
                sound.IsChecked = false;
                bool off = !draft.Settings.Beep;
                sound.IsChecked = true;
                Require(off && draft.Settings.Beep, "start-sound-setting-edits-draft");
                CheckBox wake = FindRegistrationControl<CheckBox>(registration, "ScheduleWakeEnabled");
                wake.IsChecked = true;
                bool on = draft.Settings.WakeUp;
                wake.IsChecked = false;
                Require(on && !draft.Settings.WakeUp && draft.Settings.Beep,
                    "wake-setting-edits-draft-without-changing-sound");
            }
            finally
            {
                registration.Close();
            }
            SchedulerRuntimeHost host = Create();
            Require(await host.StartAsync(), "native-owner-start");
            await WaitForRuntimeHost(() => host.Executions.Count == 2 && host.ActiveCount == 0);
            Require(host.WindowObservationError is null, "native-complete-observation");
            Require(!target.IsVisible && host.Executions.All(execution =>
                execution.Dispatch.Request.WindowTrigger?.Target.Handle == unchecked((ulong)(nuint)handle)),
                "hidden-window-captured-in-both-modes");
            SchedulerWindowTarget captured = host.Executions[0].Dispatch.Request.WindowTrigger!.Target;
            Require(host.Executions.All(execution => execution.Result is
            { Started: true, ResourcesRecovered: true, ExitCode: 0 }), "both-modes-finished");
            Require(startSounds == 2 && host.NotificationError is IOException,
                "start-sound-once-per-mode-and-failure-isolated");
            foreach (SchedulerExecutionMode mode in new[] { SchedulerExecutionMode.SameProcess,
                SchedulerExecutionMode.SeparateProcess })
            {
                Require(File.ReadAllText(Path.Combine(directory, mode + ".txt")).Trim()
                    == Environment.ProcessId.ToString(CultureInfo.InvariantCulture), mode + "-captured-getid");
            }
            await Task.Delay(1100);
            Require(host.Executions.Count == 2, "retained-window-does-not-repeat");
            Require(await host.StopAsync(TimeSpan.FromSeconds(20)), "native-stop");
            host.Dispose();
            hosts.Remove(host);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<IReadOnlyList<WindowsObservedWindow>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            delayedRelease = release;
            int calls = 0;
            bool offUi = false;
            SchedulerRuntimeHost delayed = Create(async _ =>
            {
                Interlocked.Increment(ref calls);
                offUi = !main.Dispatcher.CheckAccess();
                entered.TrySetResult();
                return await release.Task;
            });
            Require(await delayed.StartAsync(), "delayed-owner-start");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(1100);
            Require(calls == 1 && offUi, "single-flight-off-ui");
            Task<bool> stopping = delayed.StopAsync(TimeSpan.FromSeconds(20));
            Require(!delayed.CanClose && !stopping.IsCompleted, "stop-waits-for-observer");
            release.SetResult([new(new(handle, captured.ProcessId, captured.ThreadId), title, "class")]);
            Require(await stopping && delayed.CanClose && delayed.Executions.Count == 0,
                "late-result-after-stop-is-rejected");
            delayed.Dispose();
            hosts.Remove(delayed);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                checks
            }));
            return 0;
        }
        finally
        {
            delayedRelease?.TrySetResult([]);
            foreach (SchedulerRuntimeHost host in hosts)
            {
                if (!await host.StopAsync(TimeSpan.FromSeconds(20)))
                {
                    throw new InvalidOperationException("窓観測ホストの回収を確認できません。");
                }
                host.Dispose();
            }
            target.Close();
            main.Close();
        }

        SchedulerRuntimeHost Create(Func<CancellationToken, Task<IReadOnlyList<WindowsObservedWindow>>>? source = null)
        {
            SchedulerRuntimeHost host = access.CreatePlaybackHost(
                settingsPath, logs, Path.Combine(directory, "monitor"),
                windowSource: source,
                startSound: () =>
                {
                    Interlocked.Increment(ref startSounds);
                    throw new IOException("開始音の失敗を注入します。");
                });
            hosts.Add(host);
            return host;
        }

        void Save(SchedulerEntry[] entries)
        {
            var settings = new SchedulerSettingsController(settingsPath);
            settings.Reload();
            settings.Save(new SchedulerSettings(entries, enabled: true, beep: true));
        }

        void Require(bool value, string name)
            => VerificationAssert.Require(checks, value, name);
    }
}
