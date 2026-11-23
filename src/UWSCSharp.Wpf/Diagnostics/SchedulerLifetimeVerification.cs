using System.IO;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerLifetimeVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "schedule.json");
        string lease = Path.Combine(directory, "schedule.monitor");
        string logs = Path.Combine(directory, "logs");
        var checks = new List<string>();
        bool discard = false;
        var owner = new MainWindow(false, confirmDiscard: () => discard) { ShowActivated = false };
        var contender = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
        var ownerAccess = new MainWindow.SchedulerVerificationAccess(owner);
        var contenderAccess =
            new MainWindow.SchedulerVerificationAccess(contender);
        MainWindow.SchedulerVerificationAccess? faultAccess = null;
        MainWindow.SchedulerVerificationAccess? corruptAccess = null;
        MainWindow? fault = null;
        MainWindow? corrupt = null;
        try
        {
            SchedulerEntry[] entries = [Entry("same", SchedulerExecutionMode.SameProcess),
                Entry("child", SchedulerExecutionMode.SeparateProcess)];
            SchedulerSettingsStore.Save(settings, new SchedulerSettings(entries, enabled: true), null);
            owner.Show();
            await owner.RefreshInputCleanupAsync();
            ownerAccess.SetDraft("未保存の原稿", "編集出力");
            await owner.InitializeSchedulerAsync(settings, lease, logs);
            await WaitForRuntimeHost(() => Ready("same") && Ready("child"));
            Require(ownerAccess.Host!.IsActive && ownerAccess.Host.ActiveCount == 2,
                "window-initialization-starts-same-and-separate");
            contender.Show();
            await contender.RefreshInputCleanupAsync();
            await contender.InitializeSchedulerAsync(settings, lease, logs);
            Require(!contenderAccess.Host!.IsActive
                && SchedulerSettingsStore.Load(settings).Settings.Enabled,
                "contender-does-not-disable-shared-settings");
            owner.Close();
            await WaitForRuntimeHost(() => !ownerAccess.IsClosing && ownerAccess.Host.CanClose);
            await owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Require(owner.IsVisible && ownerAccess.DraftText == "未保存の原稿"
                && ownerAccess.OutputText == "編集出力" && ownerAccess.IsModified,
                "close-cancels-both-and-preserves-declined-unsaved-draft");
            Require(ownerAccess.Host.Executions.Count == 2
                && ownerAccess.Host.Executions.All(value => value.Result?.ResourcesRecovered == true),
                "close-waits-for-both-execution-resources");
            Require(!contenderAccess.Host.IsActive, "contender-does-not-automatically-take-over");
            File.Delete(Path.Combine(directory, "same.ready"));
            File.Delete(Path.Combine(directory, "child.ready"));
            await ownerAccess.RestartMonitorAsync();
            ownerAccess.Host.Poll();
            Require(ownerAccess.Host.IsActive && ownerAccess.Host.ActiveCount == 0
                && !Ready("same") && !Ready("child"), "explicit-restart-does-not-repeat-startup-rows");
            discard = true;
            owner.Close();
            await WaitForRuntimeHost(() => ownerAccess.IsClosed);
            await contenderAccess.RestartMonitorAsync();
            await WaitForRuntimeHost(() => Ready("same") && Ready("child"));
            Require(contenderAccess.Host.IsActive && contenderAccess.Host.ActiveCount == 2,
                "explicit-successor-runs-its-first-startup-rows");
            contender.Close();
            await WaitForRuntimeHost(() => contenderAccess.IsClosed);

            string broken = Path.Combine(directory, "broken.json");
            File.WriteAllText(broken, "{");
            corrupt = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
            corruptAccess =
                new MainWindow.SchedulerVerificationAccess(corrupt);
            corrupt.Show();
            await corrupt.RefreshInputCleanupAsync();
            await corrupt.InitializeSchedulerAsync(broken, Path.Combine(directory, "broken.monitor"), logs);
            Require(corruptAccess.SettingsLoadError is not null && !corruptAccess.Host!.OwnsMonitor
                && corruptAccess.CanPlay, "bad-settings-block-only-monitoring");
            corrupt.Close();
            await WaitForRuntimeHost(() => corruptAccess.IsClosed);

            string faultSettings = Path.Combine(directory, "fault.json");
            string faultLease = Path.Combine(directory, "fault.monitor");
            SchedulerEntry entry = Entry("fault", SchedulerExecutionMode.SeparateProcess);
            WriteRuntimeHostScript(entry.Path, "KBD(118,CLICK)");
            SchedulerSettingsStore.Save(faultSettings, new SchedulerSettings([entry], enabled: true), null);
            int confirmations = 0;
            bool permitExit = false;
            bool permitDiscard = false;
            fault = new MainWindow(false, confirmDiscard: () => permitDiscard) { ShowActivated = false };
            faultAccess =
                new MainWindow.SchedulerVerificationAccess(fault);
            fault.Show();
            await fault.RefreshInputCleanupAsync();
            await fault.InitializeSchedulerAsync(faultSettings, faultLease, logs,
                () => { confirmations++; return permitExit; },
                (_, _, _) => Task.FromException(new IOException("検証用の親調停失敗")));
            await WaitForRuntimeHost(() => faultAccess.Host!.Executions.Any(value => value.Result is not null));
            faultAccess.RefreshInputState();
            Require(!faultAccess.CanPlay && !faultAccess.CanRecord,
                "unrecovered-worker-blocks-interactive-buttons");
            faultAccess.SetDraft("回収未確認時の未保存原稿");
            fault.Close();
            await WaitForRuntimeHost(() => faultAccess.IsExitHeld && !faultAccess.IsClosing);
            Require(fault.IsVisible && confirmations == 0, "first-unrecovered-close-is-held");
            fault.Close();
            Require(fault.IsVisible && confirmations == 1, "declined-explicit-exit-keeps-owner");
            permitExit = true;
            fault.Close();
            await fault.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Require(fault.IsVisible && confirmations == 2 && !faultAccess.IsCloseReady,
                "explicit-exit-does-not-approve-unsaved-discard");
            permitDiscard = true;
            fault.Close();
            await WaitForRuntimeHost(() => faultAccess.IsClosed);
            Require(confirmations == 3 && faultAccess.IsAbandoned
                && faultAccess.Host!.OwnsMonitor
                && faultAccess.Host.Executions.Single().Result?.ResourcesRecovered == false,
                "explicit-exit-retains-unrecovered-owner-until-process-exit");
            using var probe = SchedulerMonitorLease.TryAcquire(faultLease);
            Require(probe is null, "closed-unrecovered-window-does-not-release-lease-early");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                checks
            }));
            return 0;
        }
        finally
        {
            discard = true;
            if (owner.IsVisible)
            {
                owner.Close();
            }
            if (contender.IsVisible)
            {
                contender.Close();
            }
            if (corrupt?.IsVisible == true)
            {
                corrupt.Close();
            }
            if (fault?.IsVisible == true)
            {
                faultAccess!.AllowExitAndDiscard();
                fault.Close();
            }
        }

        bool Ready(string name) => File.Exists(Path.Combine(directory, name + ".ready"));

        SchedulerEntry Entry(string name, SchedulerExecutionMode mode)
        {
            string path = Path.Combine(directory, name + ".uws");
            WriteRuntimeHostScript(path, $"DIM f=FOPEN(\"{name}.ready\",F_WRITE8)\n"
                + "FPUT(f,1)\nFCLOSE(f)\nSLEEP(60)");
            return new SchedulerEntry(Guid.NewGuid(), name, path, [], mode,
                new SchedulerStartupCondition(), directory);
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
