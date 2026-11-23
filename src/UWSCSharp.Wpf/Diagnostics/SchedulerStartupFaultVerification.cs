using System.Diagnostics;
using System.IO;
using System.Text.Json;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

/// <summary>
/// 接続しない実子プロセスで、初期通信の終了条件を検査します。
/// </summary>
internal static class SchedulerStartupFaultVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        string executable = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
        string settings = Path.Combine(directory, "schedule.json");
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([], enabled: true), null);
        SchedulerNativeMonitorResources? native = null;
        using var monitor = new SchedulerMonitorSession(
            new SchedulerSettingsController(settings), new SchedulerApplicationSession(),
            new SchedulerExecutionGate(), Path.Combine(directory, "monitor"), lease =>
            {
                native = new SchedulerNativeMonitorResources(lease);
                return native;
            });
        if (!await monitor.TryStartAsync(DateTimeOffset.UtcNow, TimeZoneInfo.Local))
        {
            throw new InvalidOperationException("実Jobを準備できません。", monitor.LastError);
        }
        var results = new List<object>();
        foreach (string mode in new[] { "exit", "cancel", "timeout" })
        {
            string logs = Path.Combine(directory, mode);
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "startup-probe"), mode == "exit" ? "exit" : "wait");
            var dispatch = new SchedulerDispatch(native!.Generation,
                new SchedulerExecutionRequest(Guid.NewGuid(),
                    new SchedulerEntry(Guid.NewGuid(), mode, Path.Combine(directory, "unused.uws"), [],
                        SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory),
                    DateTimeOffset.UtcNow));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var elapsed = Stopwatch.StartNew();
            Task<SchedulerWorkerRunResult> running = SchedulerWorkerClient.RunAsync(
                native.Group, executable, dispatch, logs, cancellation.Token);
            if (mode == "cancel")
            {
                while (!File.Exists(Path.Combine(logs, "ready")))
                {
                    await Task.Delay(25, cancellation.Token);
                }
                // この時点で取消し、10秒の通信期限を待たずに戻ることを確認します。
                cancellation.Cancel();
            }
            SchedulerWorkerRunResult result = await running.WaitAsync(TimeSpan.FromSeconds(
                mode == "timeout" ? 20 : 5));
            Require(!result.Started && result.Forced && result.ProcessesRecovered
                && !result.ResultKnown && !result.ResourcesRecovered && result.Completion is null
                && result.Result.Outcome == SchedulerExecutionOutcome.Unknown
                && result.ProcessExitCode is not null, mode + "-unknown-before-start");
            Require(native.Group.ActiveProcesses == 0, mode + "-process-drained");
            if (mode == "exit")
            {
                Require(result.ProcessExitCode == 23
                    && result.Diagnostic?.Contains("接続前に終了") == true, "exit-detected");
            }
            if (mode == "timeout")
            {
                Require(elapsed.Elapsed >= SchedulerWorkerProtocol.CommunicationTimeout,
                    "timeout-not-early");
            }
            results.Add(new
            {
                mode,
                elapsed_ms = elapsed.ElapsedMilliseconds,
                result.ProcessId,
                result.ProcessExitCode,
                result.Started,
                result.Forced,
                result.ProcessesRecovered,
                result.ResourcesRecovered,
                result.Diagnostic
            });
        }
        monitor.StopAccepting();
        Require(!monitor.OwnsMonitor, "monitor-released");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = "Passed",
            process_id = Environment.ProcessId,
            cases = results
        }));
        return 0;
    }
}
