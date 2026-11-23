using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 実workerの異常終了に伴う通信断と、他実行からの回収の独立性を検査します。
/// </summary>
internal static class SchedulerWorkerExitVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        string probe = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
        string settings = Path.Combine(directory, "schedule.json");
        string logs = Path.Combine(directory, "logs");
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
            throw new InvalidOperationException("実監視資源を準備できません。", monitor.LastError);
        }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var held = new List<Process>();
        var running = new List<Task<SchedulerWorkerRunResult>>();
        var checks = new List<string>();
        try
        {
            string broken = Path.Combine(directory, "broken.uws");
            string healthy = Path.Combine(directory, "healthy.uws");
            string descendants = Path.Combine(directory, "descendants.json");
            string command = $"\"{probe}\" --scheduler-job-probe spawn \"{descendants}\" \"{Path.Combine(directory, "descendants.release")}\"";
            string expression = string.Join("+CHR(34)+", command.Split('"').Select(part => "\"" + part + "\""));
            File.WriteAllText(broken, $"EXEC({expression},FALSE)\nSLEEP(120)\n", new UTF8Encoding(true));
            File.WriteAllText(healthy, "DIM f=FOPEN(\"healthy.ready\",F_WRITE8)\nFCLOSE(f)\n"
                + "WHILE !FOPEN(\"healthy.go\",F_EXISTS)\nSLEEP(0.02)\nWEND\n"
                + "PRINT \"healthy-after-peer-fault\"", new UTF8Encoding(true));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int brokenPid = 0;
            int healthyPid = 0;
            Task<SchedulerWorkerRunResult> brokenRun = Run(broken,
                pid => brokenPid = pid, () => started.TrySetResult());
            Task<SchedulerWorkerRunResult> healthyRun = Run(healthy, pid => healthyPid = pid);
            await started.Task.WaitAsync(lifetime.Token);
            await WaitFor(() => File.Exists(descendants)
                && File.Exists(Path.Combine(directory, "healthy.ready")));
            await using var stream = new FileStream(descendants, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument children = await JsonDocument.ParseAsync(stream, cancellationToken: lifetime.Token);
            int childPid = children.RootElement.GetProperty("pid").GetInt32();
            int grandchildPid = children.RootElement.GetProperty("child_pid").GetInt32();
            Process brokenProcess = Hold(brokenPid);
            Process child = Hold(childPid);
            Process grandchild = Hold(grandchildPid);
            Process healthyProcess = Hold(healthyPid);
            IReadOnlyList<uint> members = native!.Group.GetProcessIds();
            Require(new[] { brokenPid, childPid, grandchildPid, healthyPid }.Distinct().Count() == 4
                && held.All(process => !process.HasExited)
                && held.All(process => members.Contains((uint)process.Id)),
                "four-live-owned-processes-with-held-handles");
            Require(!brokenRun.IsCompleted && !healthyRun.IsCompleted,
                "two-live-executions-before-peer-exit");
            // 通信路を持つworkerだけを停止し、子孫の停止は製品の回収経路へ任せます。
            brokenProcess.Kill(entireProcessTree: false);
            SchedulerWorkerRunResult result = await brokenRun.WaitAsync(TimeSpan.FromSeconds(20));
            Require(result.Started && result.Forced && result.ProcessesRecovered
                && result.Completion is null && !result.ResultKnown
                && result.Result.Outcome == SchedulerExecutionOutcome.Unknown
                && !result.ResourcesRecovered && !result.Result.ResourcesRecovered
                && result.ProcessExitCode is not null && !string.IsNullOrEmpty(result.Diagnostic),
                "started-peer-exit-remains-unknown-without-input-acknowledgement");
            Require(brokenProcess.HasExited && child.HasExited && grandchild.HasExited,
                "worker-and-two-descendants-exited-before-result-return");
            Require(!healthyRun.IsCompleted && !healthyProcess.HasExited,
                "parallel-worker-survives-peer-recovery");
            Require(native.Group.GetProcessIds().SequenceEqual([(uint)healthyPid]),
                "only-parallel-worker-remains-in-monitor-job");
            File.WriteAllText(Path.Combine(directory, "healthy.go"), "go");
            SchedulerWorkerRunResult completed = await healthyRun.WaitAsync(TimeSpan.FromSeconds(20));
            Require(completed.Started && completed.ResultKnown && completed.ResourcesRecovered
                && completed.Result.Outcome == SchedulerExecutionOutcome.Succeeded
                && completed.Completion?.ExitCode == 0 && healthyProcess.HasExited,
                "parallel-worker-completes-normally-after-peer-fault");
            Require(native.Group.ActiveProcesses == 0, "all-owned-processes-drained");
            monitor.StopAccepting();
            Require(!monitor.OwnsMonitor, "monitor-released-after-drain");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                broken_pid = brokenPid,
                child_pid = childPid,
                grandchild_pid = grandchildPid,
                healthy_pid = healthyPid,
                result.Diagnostic,
                checks
            }));
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(running);
            foreach (Process process in held)
            {
                process.Dispose();
            }
        }

        Task<SchedulerWorkerRunResult> Run(string path, Action<int> ready, Action? started = null)
        {
            var dispatch = new SchedulerDispatch(native!.Generation,
                new SchedulerExecutionRequest(Guid.NewGuid(),
                    new SchedulerEntry(Guid.NewGuid(), Path.GetFileName(path), path, [],
                        SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory),
                    DateTimeOffset.UtcNow));
            Task<SchedulerWorkerRunResult> task = SchedulerWorkerClient.RunAsync(native.Group,
                Environment.ProcessPath!, dispatch, logs, lifetime.Token, ready, onStarted: started);
            running.Add(task);
            return task;
        }

        Process Hold(int pid)
        {
            var process = Process.GetProcessById(pid);
            held.Add(process);
            // 終了確認まで実ハンドルを保持し、PIDの再利用と区別します。
            _ = process.Handle;
            return process;
        }

        async Task WaitFor(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(25, lifetime.Token);
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
