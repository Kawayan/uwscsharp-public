using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerPreparationCloseVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "schedule.json");
        string lease = Path.Combine(directory, "schedule.monitor");
        string ready = Path.Combine(directory, "startup.ready");
        string source = Path.Combine(directory, "startup.uws");
        WriteRuntimeHostScript(source, "DIM f=FOPEN(\"startup.ready\",F_WRITE8)\n"
            + "FPUT(f,1)\nFCLOSE(f)\nSLEEP(30)");
        var entry = new SchedulerEntry(Guid.NewGuid(), "開始準備中の終了", source, [],
            SchedulerExecutionMode.SameProcess, new SchedulerStartupCondition(), directory);
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true), null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new List<string>();
        int preparationCount = 0;
        int discardCount = 0;
        bool permitDiscard = false;
        var main = new MainWindow(false, confirmDiscard: () =>
        {
            discardCount++;
            return permitDiscard;
        })
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("開始準備中の未保存原稿");
            Task initialization = main.InitializeSchedulerAsync(settings, lease,
                Path.Combine(directory, "logs"), preparationCheck: async token =>
                {
                    Interlocked.Increment(ref preparationCount);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(access.IsStarting && !initialization.IsCompleted
                && access.Host!.OwnsMonitor && !access.Host.IsActive,
                "real-resources-owned-before-runtime-start");
            Require(!File.Exists(ready) && access.Host!.Executions.Count == 0,
                "startup-row-not-dispatched-during-preparation");
            main.Close();
            Require(main.IsVisible && access.IsClosing && access.IsStarting,
                "close-during-preparation-enters-stop-wait");
            main.Close();
            Require(main.IsVisible && preparationCount == 1 && discardCount == 0,
                "repeated-close-does-not-repeat-preparation-or-approve-discard");
            using (var contender = SchedulerMonitorLease.TryAcquire(lease))
            {
                Require(contender is null, "preparation-close-retains-monitor-until-release");
            }
            release.SetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForRuntimeHost(() => !access.IsClosing && access.Host!.CanClose);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(main.IsVisible && discardCount == 1 && access.IsModified
                && access.DraftText == "開始準備中の未保存原稿",
                "completion-of-stop-still-respects-declined-discard");
            Require(!File.Exists(ready) && access.Host!.Executions.Count == 0
                && !access.Host.OwnsMonitor, "cancelled-preparation-never-starts-runtime");
            using (var successor = SchedulerMonitorLease.TryAcquire(lease))
            {
                Require(successor is not null, "monitor-released-after-preparation-and-resource-disposal");
            }
            await access.RestartMonitorAsync();
            await WaitForRuntimeHost(() => File.Exists(ready));
            Require(access.Host!.IsActive && access.Host.ActiveCount == 1
                && preparationCount == 2,
                "explicit-restart-runs-startup-row-not-admitted-by-cancelled-preparation");
            permitDiscard = true;
            main.Close();
            await WaitForRuntimeHost(() => access.IsClosed);
            Require(discardCount == 2 && access.Host.CanClose
                && access.Host.Executions.Single().Result!.ResourcesRecovered,
                "second-close-cancels-real-run-and-reclaims-owner");
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
            release.TrySetResult();
            permitDiscard = true;
            if (main.IsVisible)
            {
                main.Close();
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
