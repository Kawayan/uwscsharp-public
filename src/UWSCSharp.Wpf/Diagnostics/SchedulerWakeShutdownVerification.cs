using System.IO;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerWakeShutdownVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "schedule.json");
        var clock = new WakeShutdownClock();
        var entry = new SchedulerEntry(Guid.NewGuid(), "復帰計画の返却待ち",
            Path.Combine(directory, "unused.uws"), [], SchedulerExecutionMode.SeparateProcess,
            new SchedulerOneShotCondition(new DateOnly(2027, 1, 1), new TimeOnly(1, 0)), directory);
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true, wakeUp: true), null);
        byte[] previous = await File.ReadAllBytesAsync(settings);
        var entered = new TaskCompletionSource<SchedulerWakePlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        int confirmations = 0;
        var checks = new List<string>();
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true, reportError: errors.Add,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        SchedulerRuntimeHost? host = null;
        var access = new MainWindow.SchedulerVerificationAccess(main);
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("終了を保留する間も保持する原稿");
            await access.InitializeAsync(settings,
                Path.Combine(directory, "monitor"),
                Path.Combine(directory, "logs"), clock,
                (_, _, _) => Task.FromException<SchedulerExecutionResult>(
                    new NotSupportedException()),
                confirmUnrecoveredExit: () =>
                {
                    confirmations++;
                    return false;
                },
                diagnostics: new SchedulerRuntimeDiagnostics(
                    WakePlanComputed: plan =>
                    {
                        if (Interlocked.Increment(ref calls) == 2)
                        {
                            entered.TrySetResult(plan);
                            if (!release.Wait(TimeSpan.FromSeconds(60)))
                            {
                                throw new TimeoutException("診断の復帰計画の返却許可がありません。");
                            }
                        }
                    }));
            host = access.Host!;
            await WaitForRuntimeHost(() => host.WakeTime is not null || host.WakeError is not null);
            Require(host.IsActive && calls == 1
                && (host.WakeError is NotSupportedException || host.WakeTime == clock.Now.AddMinutes(59)),
                "first-real-wake-plan-applied-or-unsupported-without-dispatch");
            clock.Now = clock.Now.AddMinutes(1);
            host.Poll();
            SchedulerWakePlan delayed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(delayed.DueAt == new DateTimeOffset(2027, 1, 1, 1, 0, 0, TimeSpan.Zero)
                && host.OwnsMonitor && host.ActiveCount == 0 && confirmations == 0,
                "second-real-plan-computed-and-return-deliberately-held");
            main.Close();
            await WaitForRuntimeHost(() => access.IsExitHeld && !access.IsClosing);
            Require(main.IsVisible && !access.IsCloseReady && !host.CanClose
                && host.LastError is TimeoutException && host.ActiveCount == 0
                && access.IsModified && access.DraftText == "終了を保留する間も保持する原稿",
                "normal-twenty-second-close-deadline-holds-window-and-draft");
            Require(host.WakeTime is null && !host.OwnsMonitor && !host.IsActive,
                "stop-clears-native-wake-request-and-monitor-ownership");
            main.Close();
            Require(confirmations == 1 && main.IsVisible && !access.IsCloseReady,
                "second-close-uses-explicit-exit-decision-and-keeps-window-on-decline");
            Exception? oldWakeError = host.WakeError;
            release.Set();
            await WaitForRuntimeHost(() => host.CanClose);
            Require(calls == 2 && host.WakeTime is null && ReferenceEquals(oldWakeError, host.WakeError)
                && host.Executions.Count == 0 && access.ScheduledCount == 0,
                "late-computed-plan-does-not-rearm-or-dispatch-after-stop");
            byte[] current = await File.ReadAllBytesAsync(settings);
            main.Close();
            await WaitForRuntimeHost(() => access.IsClosed);
            Require(previous.SequenceEqual(current) && confirmations == 1 && errors.Count == 0,
                "returned-plan-allows-normal-close-without-consuming-reservation");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                calls,
                confirmations,
                checks
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt + ".conditions", JsonSerializer.Serialize(new
            {
                calls,
                confirmations,
                checks,
                visible = main.IsVisible,
                closing = access.IsClosing,
                held = access.IsExitHeld,
                last_error = host?.LastError?.ToString(),
                wake_error = host?.WakeError?.ToString(),
                error = error.ToString()
            }));
            throw;
        }
        finally
        {
            release.Set();
            if (host is not null && !access.IsClosed && !access.IsClosing)
            {
                await host.StopAsync(TimeSpan.FromSeconds(20));
                main.Close();
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private sealed class WakeShutdownClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
