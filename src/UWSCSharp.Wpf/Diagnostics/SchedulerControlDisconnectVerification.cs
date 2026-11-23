using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 実workerの生存中に、制御パイプだけを閉じた場合の終了を検査します。
/// </summary>
internal static class SchedulerControlDisconnectVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        string settings = Path.Combine(directory, "schedule.json");
        string script = Path.Combine(directory, "waiting.uws");
        string logs = Path.Combine(directory, "logs");
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([], enabled: true), null);
        File.WriteAllText(script, "DIM f=FOPEN(\"script.ready\",F_WRITE8)\nFCLOSE(f)\n"
            + "SLEEP(120)\nf=FOPEN(\"after-sleep.bad\",F_WRITE8)\nFCLOSE(f)\n", new UTF8Encoding(true));
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
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var heartbeatLifetime = new CancellationTokenSource();
        using var keyboardLifetime = new CancellationTokenSource();
        using var control = new SchedulerWorkerPipe();
        using var keys = new SchedulerWorkerPipe();
        var identity = new SchedulerWorkerIdentity(native!.Generation, Guid.NewGuid());
        using SchedulerChildProcess child = native.Group.Start(Environment.ProcessPath!,
            ["--scheduler-worker", control.Name, identity.Generation.ToString("N"),
                identity.OccurrenceId.ToString("N"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                logs, keys.Name], directory, lifetime.Token, new SchedulerProcessCreation());
        using var process = Process.GetProcessById(checked((int)child.Id));
        _ = process.Handle;
        using var channel = new SchedulerWorkerChannel(control.Stream, identity);
        Task heartbeat = Task.CompletedTask;
        Task keyboardTask = Task.CompletedTask;
        bool drained = false;
        var checks = new List<string>();
        try
        {
            Task keyboardConnection = keys.WaitForConnectionAsync(lifetime.Token);
            await control.WaitForConnectionAsync(lifetime.Token);
            var dispatch = new SchedulerDispatch(identity.Generation,
                new SchedulerExecutionRequest(identity.OccurrenceId,
                    new SchedulerEntry(Guid.NewGuid(), "waiting", script, [],
                        SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory),
                    DateTimeOffset.UtcNow));
            await channel.SendAsync(SchedulerWorkerMessage.Request.FromDispatch(dispatch), lifetime.Token);
            await keyboardConnection;
            Require(SchedulerPipeIdentity.Client(control.Stream.SafePipeHandle) == child.Id
                && SchedulerPipeIdentity.Client(keys.Stream.SafePipeHandle) == child.Id,
                "two-connections-authenticated-to-actual-worker");
            var keyboard = new SchedulerKeyboardServer(keys.Stream, identity, null);
            keyboardTask = keyboard.RunAsync(keyboardLifetime.Token);
            var handshake = new SchedulerWorkerHandshake(identity, process.Id);
            handshake.AcceptReady(await channel.ReadAsync(lifetime.Token) as SchedulerWorkerMessage.Ready
                ?? throw new InvalidDataException("準備通知がありません。"));
            await channel.SendAsync(handshake.CreateNextCommand(), lifetime.Token);
            heartbeat = channel.RunHeartbeatAsync(heartbeatLifetime.Token);
            SchedulerWorkerMessage message;
            do
            {
                message = await channel.ReadAsync(lifetime.Token);
            }
            while (message is SchedulerWorkerMessage.Heartbeat);
            handshake.AcceptStarted(message as SchedulerWorkerMessage.Started
                ?? throw new InvalidDataException("開始通知がありません。"));
            Require(handshake.Phase == SchedulerWorkerPhase.Running, "worker-started-acknowledged");
            while (!File.Exists(Path.Combine(directory, "script.ready")))
            {
                await Task.Delay(25, lifetime.Token);
            }
            Require(!process.HasExited && control.Stream.IsConnected && keys.Stream.IsConnected
                && native.Group.GetProcessIds().Contains(child.Id), "live-script-before-control-only-disconnect");
            var elapsed = Stopwatch.StartNew();
            // 取消通知やプロセス停止を送らず、制御側だけを閉じます。
            control.Dispose();
            heartbeatLifetime.Cancel();
            uint code = await child.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(20));
            elapsed.Stop();
            Require(code == 1 && process.HasExited
                && !File.Exists(Path.Combine(directory, "after-sleep.bad")),
                "worker-exits-with-error-without-parent-terminate-or-cancel");
            Require(!keys.Stream.SafePipeHandle.IsClosed, "keyboard-server-endpoint-kept-open");
            await keyboardTask.WaitAsync(TimeSpan.FromSeconds(5));
            Require(keyboard.Failure is null && keyboard.Recovered,
                "idle-keyboard-channel-closes-without-unacknowledged-transaction");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForDrainAsync(cleanup.Token);
            drained = true;
            Require(native.Group.ActiveProcesses == 0, "owned-process-drained");
            child.Dispose();
            monitor.StopAccepting();
            Require(!monitor.OwnsMonitor, "monitor-released-after-drain");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                worker_pid = process.Id,
                exit_code = code,
                disconnect_to_exit_ms = elapsed.ElapsedMilliseconds,
                checks
            }));
            return 0;
        }
        finally
        {
            heartbeatLifetime.Cancel();
            keyboardLifetime.Cancel();
            control.Dispose();
            if (!drained)
            {
                // 失敗時だけ所有する検証用の子を回収し、成功の根拠には使いません。
                child.TerminateTree();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await child.WaitForDrainAsync(cleanup.Token);
            }
            await keyboardTask;
            try
            {
                await heartbeat;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException
                or ObjectDisposedException)
            {
                App.ReportCommandLineError(error);
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
