using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 背景ゲストで、実WPFworkerと所有Jobの接続を検査します。
/// </summary>
internal static class SchedulerWorkerVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string targets = Path.Combine(directory, "targets");
        Directory.CreateDirectory(targets);
        string logs = Path.Combine(directory, "logs");
        string leasePath = Path.Combine(directory, "schedule.monitor");
        string settingsPath = Path.Combine(directory, "schedule.json");
        SchedulerSettingsStore.Save(settingsPath,
            new SchedulerSettings([], enabled: true), null);
        SchedulerNativeMonitorResources? nativeResources = null;
        using var monitor = new SchedulerMonitorSession(
            new SchedulerSettingsController(settingsPath), new SchedulerApplicationSession(),
            new SchedulerExecutionGate(), leasePath, lease =>
            {
                nativeResources = new SchedulerNativeMonitorResources(lease);
                return nativeResources;
            });
        if (!await monitor.TryStartAsync(DateTimeOffset.UtcNow, TimeZoneInfo.Local))
        {
            throw new InvalidOperationException("実Jobを持つ監視を開始できません。", monitor.LastError);
        }
        SchedulerProcessGroup group = nativeResources!.Group;
        bool resourcesReleased = false;
        string executable = Environment.ProcessPath!;
        string currentDirectory = Environment.CurrentDirectory;
        var checks = new List<string>();
        int completed = 0;
        var running = new List<Task<SchedulerWorkerRunResult>>();
        using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Exception? primary = null;
        try
        {
            Require(monitor.OwnsMonitor && monitor.IsActive, "monitor-resource-prepared");
            string first = Path.Combine(targets, "first.uws");
            Write(first, "DIM f=FOPEN(\"before-start.bad\",F_WRITE8)\nFCLOSE(f)");
            SchedulerDispatch firstDispatch = Dispatch(first, "", "日本語 \"quote\" \\");
            const string fresh = """
                DIM f=FOPEN("first.data",F_WRITE8)
                FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))
                FPUT(f,IDTOHND(GETID(GET_THISUWSC_WIN)))
                FPUT(f,IDTOHND(GETID(GET_LOGPRINT_WIN)))
                FPUT(f,IDTOHND(GETID(GET_STOPFORM_WIN)))
                FPUT(f,LENGTH(PARAM_STR))
                FPUT(f,PARAM_STR[0])
                FPUT(f,PARAM_STR[1])
                FCLOSE(f)
                f=FOPEN("first.ready",F_WRITE8)
                FCLOSE(f)
                PRINT "fresh-worker"
                PRINT PARAM_STR[1]
                DIM sentinel=42
                WHILE !FOPEN("first.go",F_EXISTS)
                    SLEEP(0.02)
                WEND
                PRINT "monitor-on-type=" + VARTYPE(POFF(P_MONIPOWER3))
                TRY
                    POFF(P_UWSC_REEXEC)
                EXCEPT
                    PRINT TRY_ERRMSG
                ENDTRY
                EXITEXIT(7)
                """;
            int firstPid = 0;
            Task<SchedulerWorkerRunResult> firstRun = Run(firstDispatch, onReady: pid =>
            {
                firstPid = pid;
                Require(!File.Exists(Path.Combine(targets, "before-start.bad")), "no-script-before-start");
                Write(first, fresh);
            });
            await WaitFor(() => File.Exists(Path.Combine(targets, "first.ready")));
            string[] values = await File.ReadAllLinesAsync(Path.Combine(targets, "first.data"));
            Require(values.Length == 7 && int.Parse(values[0], CultureInfo.InvariantCulture) == firstPid
                && firstPid != Environment.ProcessId, "actual-separate-pid");
            nint[] handles = [.. values.Skip(1).Take(3).Select(value => (nint)long.Parse(value, CultureInfo.InvariantCulture))];
            Require(handles.All(handle => handle != 0) && handles.Distinct().Count() == 3,
                "three-independent-window-handles");
            foreach (nint handle in handles)
            {
                Native.GetWindowThreadProcessId(handle, out uint pid);
                Require(pid == (uint)firstPid, "window-owned-by-worker-" + checks.Count);
            }
            Require(values[4] == "2" && values[5] == "" && values[6] == "日本語 \"quote\" \\",
                "ordered-empty-unicode-arguments");
            File.WriteAllText(Path.Combine(targets, "first.go"), "go");
            SchedulerWorkerRunResult firstResult = await firstRun;
            Known(firstResult, 7, "first");
            string log = new SchedulerManagedStorage(logs).LogPath(firstDispatch.Request.OccurrenceId);
            string firstLog = await File.ReadAllTextAsync(log);
            Require(firstLog.Contains("fresh-worker", StringComparison.Ordinal),
                "source-read-at-start-and-own-log");
            Require(firstLog.Contains("monitor-on-type=0", StringComparison.Ordinal)
                && firstLog.Contains("予約実行ではアプリを再起動できません。",
                    StringComparison.Ordinal), "worker-power-and-restart-rejection");
            Require(Environment.CurrentDirectory == currentDirectory
                && !File.Exists(Path.Combine(targets, "UWSC.LOG")), "directory-and-default-log-isolation");

            string profile = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
            byte[]? savedProfile = File.Exists(profile)
                ? await File.ReadAllBytesAsync(profile) : null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                await File.WriteAllTextAsync(profile, "[SET]\nDefaultFont=Arial,28\n");
                string fontPath = Path.Combine(targets, "worker-font.uws");
                string title = "worker-font-" + Guid.NewGuid().ToString("N");
                Write(fontPath, $$"""
                    OPTION DLGTITLE="{{title}}-ini"
                    MSGBOX("configured")
                    OPTION DEFAULTFONT="Arial,28"
                    OPTION DLGTITLE="{{title}}-explicit"
                    MSGBOX("configured")
                    OPTION DEFAULTFONT="Arial,9"
                    OPTION DLGTITLE="{{title}}-override"
                    MSGBOX("configured")
                    """);
                int fontPid = 0;
                Task<SchedulerWorkerRunResult> fontRun =
                    Run(Dispatch(fontPath), onReady: pid => fontPid = pid);
                int iniHeight = await SchedulerFontVerification
                    .ReadButtonHeightAsync(title + "-ini", () => fontPid);
                int explicitHeight = await SchedulerFontVerification
                    .ReadButtonHeightAsync(title + "-explicit", () => fontPid);
                int overrideHeight = await SchedulerFontVerification
                    .ReadButtonHeightAsync(title + "-override", () => fontPid);
                Require(iniHeight == explicitHeight && iniHeight > overrideHeight,
                    $"worker-font-ini-and-override: {iniHeight}/{explicitHeight}/{overrideHeight}");
                Known(await fontRun, 0, "worker-font-complete");
            }
            finally
            {
                if (savedProfile is null)
                {
                    File.Delete(profile);
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, savedProfile);
                }
            }

            string variables = Path.Combine(targets, "variables.uws");
            Write(variables, "PRINT sentinel");
            Known(await Run(Dispatch(variables)), 1, "independent-variables");
            Known(await Run(Dispatch(Path.Combine(targets, "missing.uws"))), 1, "missing-source");

            string logging = Path.Combine(targets, "logging.uws");
            Write(logging, "PRINT \"log-failure-success\"");
            string blockedLogs = Path.Combine(directory, "blocked-logs");
            File.WriteAllText(blockedLogs, "file");
            SchedulerWorkerRunResult logResult = await Run(Dispatch(logging), blockedLogs);
            Known(logResult, 0, "log-failure");
            Require(logResult.Completion!.Diagnostic?.Contains("予約ログ") == true,
                "logging-failure-separate-from-exit");

            string early = Path.Combine(targets, "early.uws");
            Write(early, "DIM f=FOPEN(\"must-not-run\",F_WRITE8)\nFCLOSE(f)");
            using (var cancel = new CancellationTokenSource())
            {
                SchedulerWorkerRunResult result = await Run(Dispatch(early), token: cancel.Token,
                    onReady: _ => cancel.Cancel());
                Require(result.ResultKnown && result.ResourcesRecovered && !result.Started
                    && result.Result.Outcome == SchedulerExecutionOutcome.Cancelled
                    && !result.Result.Started
                    && result.Completion is { ExitCode: 130, CancellationCompleted: true }
                    && !File.Exists(Path.Combine(targets, "must-not-run")), "cancel-before-start");
            }

            string cooperative = Path.Combine(targets, "cooperative.uws");
            Write(cooperative, "DIM f=FOPEN(\"cooperative.ready\",F_WRITE8)\nFCLOSE(f)\nSLEEP(30)");
            using (var cancel = new CancellationTokenSource())
            {
                Task<SchedulerWorkerRunResult> task = Run(Dispatch(cooperative), token: cancel.Token);
                await WaitFor(() => File.Exists(Path.Combine(targets, "cooperative.ready")));
                cancel.Cancel();
                SchedulerWorkerRunResult result = await task;
                Known(result, 130, "cooperative-cancel");
                Require(result.Completion!.CancellationCompleted
                    && result.Result.Outcome == SchedulerExecutionOutcome.Cancelled
                    && result.Result.Started, "cancel-acknowledged");
            }

            string heartbeat = Path.Combine(targets, "heartbeat.uws");
            Write(heartbeat, "SLEEP(12)\nPRINT \"heartbeat-alive\"");
            Known(await Run(Dispatch(heartbeat)), 0, "longer-than-frame-timeout");

            string blocked = Path.Combine(targets, "ui-blocked.uws");
            Write(blocked, "DIM f=FOPEN(\"ui-blocked.data\",F_WRITE8)\n"
                + "FPUT(f,IDTOHND(GETID(GET_THISUWSC_WIN)))\nFCLOSE(f)\n"
                + "f=FOPEN(\"ui-blocked.ready\",F_WRITE8)\nFCLOSE(f)\nSLEEP(30)");
            using (var cancel = new CancellationTokenSource())
            {
                int pid = 0;
                Task<SchedulerWorkerRunResult> task = Run(Dispatch(blocked), token: cancel.Token,
                    onReady: id => pid = id);
                await WaitFor(() => File.Exists(Path.Combine(targets, "ui-blocked.ready")));
                nint window = (nint)long.Parse((await File.ReadAllLinesAsync(
                    Path.Combine(targets, "ui-blocked.data")))[0], CultureInfo.InvariantCulture);
                uint thread = Native.GetWindowThreadProcessId(window, out uint ownerPid);
                Require(ownerPid == (uint)pid, "suspend-owned-worker-only");
                using SafeFileHandle handle = OpenThread(0x802, false, thread);
                if (handle.IsInvalid || GetProcessIdOfThread(handle) != (uint)pid)
                {
                    throw new InvalidOperationException("停止対象のスレッド所有者を確認できません。");
                }
                uint previous = SuspendThread(handle);
                if (previous == uint.MaxValue)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                try
                {
                    cancel.Cancel();
                    SchedulerWorkerRunResult result = await task;
                    Require(result.Forced && result.ProcessesRecovered && !result.ResultKnown
                        && result.Result.Outcome == SchedulerExecutionOutcome.Unknown
                        && result.Result.Forced && !result.Result.ResourcesRecovered
                        && !result.ResourcesRecovered && result.Completion is null,
                        "unresponsive-ui-forced-after-grace");
                    Require(!Native.IsWindow(window), "forced-worker-window-reclaimed");
                }
                finally
                {
                    if (Native.IsWindow(window))
                    {
                        ResumeThread(handle);
                    }
                }
            }

            string a = Path.Combine(targets, "parallel-a.uws");
            string b = Path.Combine(targets, "parallel-b.uws");
            foreach (string key in new[] { "a", "b" })
            {
                Write(key == "a" ? a : b, "PRINT PARAM_STR[0]\n"
                    + $"DIM f=FOPEN(\"parallel-{key}.ready\",F_WRITE8)\n"
                    + "FCLOSE(f)\n"
                    + $"WHILE !FOPEN(\"parallel-{key}.go\",F_EXISTS)\nSLEEP(0.02)\nWEND");
            }
            using (var cancel = new CancellationTokenSource())
            {
                SchedulerDispatch da = Dispatch(a, "isolated-a");
                SchedulerDispatch db = Dispatch(b, "isolated-b");
                Task<SchedulerWorkerRunResult> ra = Run(da, token: cancel.Token);
                Task<SchedulerWorkerRunResult> rb = Run(db);
                await WaitFor(() => File.Exists(Path.Combine(targets, "parallel-a.ready"))
                    && File.Exists(Path.Combine(targets, "parallel-b.ready")));
                cancel.Cancel();
                SchedulerWorkerRunResult ar = await ra;
                Known(ar, 130, "parallel-cancel-a");
                Require(!rb.IsCompleted, "parallel-b-survives-cancel-a");
                monitor.StopAccepting();
                Require(monitor.OwnsMonitor && !monitor.IsActive,
                    "monitor-lease-held-by-live-worker");
                bool rejected = false;
                try
                {
                    monitor.Dispose();
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                Require(rejected, "monitor-dispose-rejected-before-job-drain");
                using (var contender = SchedulerMonitorLease.TryAcquire(leasePath))
                {
                    Require(contender is null, "monitor-contender-rejected-before-job-drain");
                }

                File.WriteAllText(Path.Combine(targets, "parallel-b.go"), "go");
                SchedulerWorkerRunResult br = await rb;
                Known(br, 0, "parallel-complete-b");
                Require(ar.ProcessId != br.ProcessId, "parallel-distinct-processes");
                string la = await File.ReadAllTextAsync(new SchedulerManagedStorage(logs).LogPath(da.Request.OccurrenceId));
                string lb = await File.ReadAllTextAsync(new SchedulerManagedStorage(logs).LogPath(db.Request.OccurrenceId));
                Require(la.Contains("isolated-a") && !la.Contains("isolated-b")
                    && lb.Contains("isolated-b") && !lb.Contains("isolated-a"), "parallel-log-isolation");
            }

            Require(group.ActiveProcesses == 0, "all-worker-descendants-drained");
            monitor.StopAccepting();
            resourcesReleased = !monitor.OwnsMonitor;
            Require(resourcesReleased, "monitor-released-after-job-disposal");
            using (var successor = SchedulerMonitorLease.TryAcquire(leasePath))
            {
                Require(successor is not null, "monitor-successor-acquires-after-drain");
            }

            Require(!System.Windows.Application.Current.Windows.OfType<MainWindow>().Any(),
                "diagnostic-parent-has-no-editor");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                completed,
                checks
            }), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            verification.Cancel();
            try
            {
                await Task.WhenAll(running);
                if (!resourcesReleased)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await group.WaitForDrainAsync(cleanup.Token);
                    monitor.StopAccepting();
                }
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["予約worker検証の回収"] = cleanup;
            }
        }

        SchedulerDispatch Dispatch(string path, params string[] arguments) =>
            new(nativeResources.Generation, new SchedulerExecutionRequest(Guid.NewGuid(),
                new SchedulerEntry(Guid.NewGuid(), Path.GetFileName(path), path, arguments,
                    SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), targets),
                DateTimeOffset.UtcNow));

        Task<SchedulerWorkerRunResult> Run(SchedulerDispatch dispatch, string? logPath = null,
            CancellationToken token = default, Action<int>? onReady = null)
        {
            Task<SchedulerWorkerRunResult> task = RunOneAsync();
            running.Add(task);
            completed++;
            return task;

            async Task<SchedulerWorkerRunResult> RunOneAsync()
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    verification.Token, token);
                return await SchedulerWorkerClient.RunAsync(group, executable,
                    dispatch, logPath ?? logs, linked.Token, onReady);
            }
        }

        void Known(SchedulerWorkerRunResult result, int code, string name) =>
            Require(result.ResultKnown && result.ResourcesRecovered && result.Started
                && result.Completion!.ExitCode == code && result.Result.ExitCode == code
                && result.Result.Outcome == (code == 0
                    ? SchedulerExecutionOutcome.Succeeded : code == 130
                    ? SchedulerExecutionOutcome.Cancelled : SchedulerExecutionOutcome.Failed),
                name + ": " + result.Diagnostic + " / " + result.Completion?.Diagnostic);

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private static void Write(string path, string source) =>
        File.WriteAllText(path, source, new UTF8Encoding(true));

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(SafeFileHandle thread);
}
