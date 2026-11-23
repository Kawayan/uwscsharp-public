using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 実プロセスの接続元と通知内PIDの不一致を、開始前の拒否とJob回収まで検査します。
/// </summary>
internal static class SchedulerIdentityVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        string executable = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
        string settings = Path.Combine(directory, "schedule.json");
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([], enabled: true), null);
        SchedulerNativeMonitorResources? native = null;
        using var monitor = new SchedulerMonitorSession(new SchedulerSettingsController(settings),
            new SchedulerApplicationSession(), new SchedulerExecutionGate(),
            Path.Combine(directory, "monitor"), lease =>
            {
                native = new SchedulerNativeMonitorResources(lease);
                return native;
            });
        if (!await monitor.TryStartAsync(DateTimeOffset.UtcNow, TimeZoneInfo.Local))
        {
            throw new InvalidOperationException("実Jobを準備できません。", monitor.LastError);
        }
        var checks = new List<string>();
        var outcomes = new List<object>();
        foreach (string mode in new[] { "foreign-control", "wrong-ready", "foreign-keyboard" })
        {
            string logs = Path.Combine(directory, mode);
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "identity-probe"), mode);
            var dispatch = new SchedulerDispatch(native!.Generation,
                new SchedulerExecutionRequest(Guid.NewGuid(), new SchedulerEntry(Guid.NewGuid(), mode,
                    Path.Combine(directory, "unused.uws"), [], SchedulerExecutionMode.SeparateProcess,
                    new SchedulerStartupCondition(), directory), DateTimeOffset.UtcNow));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            int authenticated = 0;
            bool ready = false;
            Task<SchedulerWorkerRunResult> running = SchedulerWorkerClient.RunAsync(native.Group,
                executable, dispatch, logs, timeout.Token, onReady: _ => ready = true,
                onControlConnected: _ => authenticated++);
            var held = new List<Process>();
            try
            {
                await WaitFor(() => File.Exists(Path.Combine(logs, "child.json"))
                    && (mode == "wrong-ready" || File.Exists(Path.Combine(logs, "peer.json"))), timeout.Token);
                string[] roles = mode == "wrong-ready" ? ["child"] : ["child", "peer"];
                foreach (string name in roles)
                {
                    using JsonDocument data = await Read(Path.Combine(logs, name + ".json"));
                    var process = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
                    _ = process.Handle;
                    held.Add(process);
                }
                Require(held.All(process => !process.HasExited)
                    && held.All(process => native.Group.GetProcessIds().Contains((uint)process.Id)),
                    mode + "-actual-peers-alive-in-owned-job");
                File.WriteAllText(Path.Combine(logs, "connect"), "go");
                SchedulerWorkerRunResult result = await running.WaitAsync(TimeSpan.FromSeconds(15));
                string expected = mode switch
                {
                    "foreign-control" => "予約workerの接続元PIDが一致しません。",
                    "wrong-ready" => "予約workerの準備通知が順序またはPIDと一致しません。",
                    _ => "キー送信の接続元PIDが一致しません。"
                };
                Require(result.Diagnostic?.Contains(expected, StringComparison.Ordinal) == true
                    && !result.Started && result.Forced && result.ProcessesRecovered
                    && !result.ResultKnown && !result.ResourcesRecovered
                    && result.Result.Outcome == SchedulerExecutionOutcome.Unknown
                    && result.Completion is null && !ready,
                    mode + "-pid-mismatch-rejected-before-start");
                Require(authenticated == (mode == "foreign-control" ? 0 : 1)
                    && File.Exists(Path.Combine(logs, "request-received")) == (mode != "foreign-control")
                    && !File.Exists(Path.Combine(logs, "start.bad")), mode + "-expected-protocol-boundary");
                Require(held.All(process => process.HasExited) && native.Group.ActiveProcesses == 0,
                    mode + "-all-owned-peers-drained");
                outcomes.Add(new
                {
                    mode,
                    held_processes = held.Count,
                    result.Diagnostic
                });
            }
            finally
            {
                timeout.Cancel();
                await running;
                foreach (Process process in held)
                {
                    process.Dispose();
                }
            }
        }
        using (var pipe = new SchedulerWorkerPipe())
        using (var keys = new SchedulerWorkerPipe())
        {
            var identity = new SchedulerWorkerIdentity(native!.Generation, Guid.NewGuid());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using SchedulerChildProcess worker = native.Group.Start(Environment.ProcessPath!,
                ["--scheduler-worker", pipe.Name, identity.Generation.ToString("N"), identity.OccurrenceId.ToString("N"),
                    "1", Path.Combine(directory, "wrong-parent"), keys.Name], directory, timeout.Token,
                new SchedulerProcessCreation());
            using var process = Process.GetProcessById(checked((int)worker.Id));
            _ = process.Handle;
            await pipe.WaitForConnectionAsync(timeout.Token);
            Require(Environment.ProcessId != 1 && SchedulerPipeIdentity.Client(pipe.Stream.SafePipeHandle) == worker.Id,
                "wrong-parent-actual-worker-connected-to-real-parent");
            uint code = await worker.WaitForExitAsync(timeout.Token);
            Require(code == 1 && process.HasExited && !keys.Stream.IsConnected,
                "wrong-parent-real-worker-rejects-before-keyboard-connection");
            await worker.WaitForDrainAsync(timeout.Token);
            Require(native.Group.ActiveProcesses == 0, "wrong-parent-process-drained-without-request");
        }
        monitor.StopAccepting();
        Require(!monitor.OwnsMonitor, "monitor-released-after-all-rejections");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = "Passed",
            process_id = Environment.ProcessId,
            outcomes,
            checks
        }));
        return 0;

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(25, token);
        }
    }

    private static async Task<JsonDocument> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }
}
