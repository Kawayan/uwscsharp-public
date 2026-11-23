using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class SchedulerSameProcessVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string targets = Path.Combine(directory, "targets");
        Directory.CreateDirectory(targets);
        string logs = Path.Combine(directory, "logs");
        var checks = new List<string>();
        var fonts = new List<SchedulerFontVerification.Observation>();
        var errors = new List<Exception>();
        var powerRequests = new List<PowerCommandRequest>();
        int discarded = 0;
        var main = new MainWindow(false, confirmDiscard: () =>
        {
            discarded++;
            return true;
        }, reportError: errors.Add,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"),
            executeSystemPowerCommand: powerRequests.Add);
        var access = new MainWindow.SchedulerVerificationAccess(main);
        var recording = new MainWindow.RecordingVerificationAccess(main);
        var controller = new SchedulerSettingsController(
            Path.Combine(directory, "schedule.json"));
        using var monitor = new SchedulerMonitorSession(controller,
            new SchedulerApplicationSession(), access.Gate,
            Path.Combine(directory, "schedule.monitor"));
        var dispatched = new List<SchedulerDispatch>();
        Task<SchedulerPlaybackResult>? running = null;
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previousProfile = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            await File.WriteAllTextAsync(profile, "[SET]\nStopSize=1000,700\n");
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("未保存の編集原稿", "編集画面の出力",
                Path.Combine(directory, "editor.uws"), "編集の保存先");
            string currentDirectory = Environment.CurrentDirectory;
            string firstPath = Path.Combine(targets, "first.uws");
            string secondPath = Path.Combine(targets, "second.uws");
            string cancelPath = Path.Combine(targets, "cancel.uws");
            string loggingPath = Path.Combine(targets, "logging.uws");
            string fontPath = Path.Combine(targets, "font.uws");
            string nextFontPath = Path.Combine(targets, "next-font.uws");
            string fontTitle = "scheduled-font-" + Guid.NewGuid().ToString("N");
            SchedulerEntry Entry(string path, params string[] arguments) =>
                new(Guid.NewGuid(), Path.GetFileName(path), path, arguments,
                    SchedulerExecutionMode.SameProcess, new SchedulerStartupCondition(), directory);
            SchedulerEntry first = Entry(firstPath, "", "日本語 \"quote\" \\");
            SchedulerEntry second = Entry(secondPath);
            SchedulerEntry cancel = Entry(cancelPath);
            SchedulerEntry missing = Entry(Path.Combine(targets, "missing.uws"));
            SchedulerEntry logging = Entry(loggingPath);
            SchedulerEntry fontEntry = Entry(fontPath);
            SchedulerEntry nextFontEntry = Entry(nextFontPath);
            await File.WriteAllTextAsync(firstPath, "PRINT \"OLD\"", new UTF8Encoding(true));
            controller.Reload();
            controller.Save(new SchedulerSettings(
                [first, second, cancel, missing, logging, fontEntry, nextFontEntry],
                enabled: true));
            string source = """
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
                PRINT "fresh"
                PRINT PARAM_STR[0]
                PRINT PARAM_STR[1]
                POFF(P_POWEROFF OR P_FORCE)
                POFF(P_MONIPOWER3)
                TRY
                    POFF(P_UWSC_REEXEC)
                EXCEPT
                    PRINT "scheduled-restart-rejected"
                ENDTRY
                DIM sentinel=42
                WHILE !FOPEN("first.go",F_EXISTS)
                    SLEEP(0.02)
                WEND
                EXITEXIT(7)
                """;
            await File.WriteAllTextAsync(firstPath, source, new UTF8Encoding(true));
            await File.WriteAllTextAsync(secondPath, "PRINT sentinel", new UTF8Encoding(true));
            await File.WriteAllTextAsync(cancelPath,
                "DIM f=FOPEN(\"cancel.ready\",F_WRITE8)\nFCLOSE(f)\nSLEEP(30)",
                new UTF8Encoding(true));
            await File.WriteAllTextAsync(loggingPath, "PRINT \"log-failure-success\"",
                new UTF8Encoding(true));
            await File.WriteAllTextAsync(fontPath,
                SchedulerFontVerification.Source(fontTitle, true), new UTF8Encoding(true));
            await File.WriteAllTextAsync(nextFontPath,
                SchedulerFontVerification.Source(fontTitle + "-next", false), new UTF8Encoding(true));
            Require(monitor.TryStart(DateTimeOffset.UtcNow, TimeZoneInfo.Local), "monitor-start");
            SchedulerDispatch Take()
            {
                SchedulerDispatch dispatch = monitor.TryDispatch()
                    ?? throw new InvalidOperationException("予約を引き渡せません。");
                dispatched.Add(dispatch);
                return dispatch;
            }

            SchedulerDispatch firstDispatch = Take();
            running = main.RunScheduledPlaybackAsync(firstDispatch, logs);
            await WaitFor(() => File.Exists(Path.Combine(targets, "first.ready")));
            string[] values = await File.ReadAllLinesAsync(Path.Combine(targets, "first.data"));
            Require(values.Length == 7 && int.Parse(values[0], CultureInfo.InvariantCulture)
                == Environment.ProcessId && values[4] == "2" && values[5] == ""
                && values[6] == first.Arguments[1], "same-pid-arguments-and-working-directory");
            nint own = new WindowInteropHelper(main).EnsureHandle();
            long[] handles = [.. values.Skip(1).Take(3).Select(value => long.Parse(value, CultureInfo.InvariantCulture))];
            Require(handles.All(handle => handle != 0 && handle != (long)own)
                && handles.Distinct().Count() == 3, "dedicated-owner-log-and-stop-windows");
            using (var dpi = new PhysicalDpiScope())
            {
                Require(Native.GetWindowRect((nint)handles[2], out Native.Rect bounds)
                    && bounds.Right - bounds.Left == 1000 && bounds.Bottom - bounds.Top == 700,
                    "scheduled-stop-size-from-startup-settings");
            }
            Require(await recording.PlayAsync() == 2, "editor-playback-blocked");
            Task? listener = null;
            try
            {
                using var instance = new InteractiveInstance();
                using var sender = new InteractiveInstance();
                Require(instance.IsFirst && !sender.IsFirst,
                    "single-instance-ownership-during-scheduled-playback");
                listener = instance.ListenAsync(main.Dispatcher,
                    main.OpenInteractiveLaunch, main.StartInteractiveLaunch);
                var request = new InteractiveInstance.RunRequest(secondPath, ["引数"]);
                Require(!await sender.RequestOpenAsync(null, request)
                    && !running.IsCompleted && discarded == 0,
                    "transferred-run-blocked-without-discard-during-scheduled-playback");
            }
            finally
            {
                if (listener is not null)
                {
                    try
                    {
                        await listener;
                    }
                    catch (OperationCanceledException)
                    {
                        // 起動要求の待機を終了します。
                    }
                }
            }
            recording.ToggleRecording();
            Require(!recording.HasRecorder && !recording.IsStarting
                && discarded == 0, "recording-blocked-before-discard-prompt");
            access.TogglePause();
            await WaitFor(() => access.IsPlaybackPaused);
            await WaitFor(() => access.PlaybackStatus == "一時停止中");
            Require(true, "pause-status-on-dedicated-control");
            access.TogglePause();
            await File.WriteAllTextAsync(Path.Combine(targets, "first.go"), "release");
            SchedulerPlaybackResult result = await running.WaitAsync(TimeSpan.FromSeconds(15));
            Require(result.ExitCode == 7 && result.ResourcesRecovered
                && result.Output.Contains("fresh", StringComparison.Ordinal)
                && !result.Output.Contains("OLD", StringComparison.Ordinal), "fresh-source-pause-and-completion");
            Require(monitor.Complete(firstDispatch, result.Result), "first-reclaimed");
            Require(powerRequests.SequenceEqual(
                [
                    new(PowerCommand.PowerOff, true, false),
                    new(PowerCommand.MonitorOn, false, false)
                ]) && result.Output.Contains("scheduled-restart-rejected",
                    StringComparison.Ordinal), "scheduled-system-power-routing");
            RequireEditorUnchanged();
            Require(File.Exists(new SchedulerManagedStorage(logs).LogPath(firstDispatch.Request.OccurrenceId))
                && !File.Exists(Path.Combine(targets, "UWSC.LOG"))
                && Environment.CurrentDirectory == currentDirectory, "isolated-log-and-process-directory");

            SchedulerDispatch secondDispatch = Take();
            result = await main.RunScheduledPlaybackAsync(secondDispatch, logs);
            Require(result.ExitCode == 1 && result.Diagnostic is not null
                && result.ResourcesRecovered, "execution-variables-not-shared");
            Require(monitor.Complete(secondDispatch, result.Result), "second-reclaimed");
            RequireEditorUnchanged();

            SchedulerDispatch cancellation = Take();
            running = main.RunScheduledPlaybackAsync(cancellation, logs);
            await WaitFor(() => File.Exists(Path.Combine(targets, "cancel.ready")));
            main.Close();
            result = await running.WaitAsync(TimeSpan.FromSeconds(15));
            Require(main.IsVisible && result.ExitCode == 130 && result.ResourcesRecovered
                && result.Result.Outcome == SchedulerExecutionOutcome.Cancelled,
                "close-cancels-and-retains-owner-window");
            Require(monitor.Complete(cancellation, result.Result), "cancel-reclaimed");
            RequireEditorUnchanged();

            SchedulerDispatch failure = Take();
            result = await main.RunScheduledPlaybackAsync(failure, logs);
            Require(result.ExitCode == 1 && result.Diagnostic is not null
                && result.ResourcesRecovered, "missing-source-isolated-failure");
            Require(monitor.Complete(failure, result.Result), "failure-reclaimed");
            RequireEditorUnchanged();
            SchedulerDispatch loggingFailure = Take();
            string blocked = Path.Combine(directory, "blocked-log-parent");
            await File.WriteAllTextAsync(blocked, "not-a-directory");
            result = await main.RunScheduledPlaybackAsync(loggingFailure, blocked);
            Require(result.ExitCode == 0 && result.ResourcesRecovered
                && result.Diagnostic is not null
                && result.Output.Contains("log-failure-success", StringComparison.Ordinal),
                "log-save-failure-does-not-change-script-success");
            Require(monitor.Complete(loggingFailure, result.Result),
                "log-failure-reclaimed");
            RequireEditorUnchanged();
            await File.WriteAllTextAsync(profile,
                "[SET]\nStopSize=1000,700\nDefaultFont=Arial,18\n");
            for (int execution = 0; execution < 2; execution++)
            {
                SchedulerDispatch fontDispatch = Take();
                running = main.RunScheduledPlaybackAsync(fontDispatch, logs);
                string title = fontTitle + (execution == 0 ? "" : "-next");
                string family = execution == 0 ? "Arial" : "Segoe UI";
                int points = execution == 0 ? 18 : 12;
                foreach (string suffix in new[] { "message", "input", "select" })
                {
                    fonts.Add(await SchedulerFontVerification.ObserveAsync(
                        title + "-" + suffix, family, points,
                        suffix == "select" ? 0x5001 : 4097));
                    Require(true, $"scheduled-font-{execution}-{suffix}");
                    if (execution == 0 && suffix == "message")
                    {
                        // 実行中は初期値を保持し、次の予約だけが新しいINIを読みます。
                        await File.WriteAllTextAsync(profile,
                            "[SET]\nStopSize=1000,700\nDefaultFont=Segoe UI,12\n");
                    }
                }

                if (execution == 0)
                {
                    fonts.Add(await SchedulerFontVerification.ObserveAsync(
                        title + "-override", "Consolas", 11, 4097));
                    Require(true, "scheduled-font-option-override");
                }

                result = await running.WaitAsync(TimeSpan.FromSeconds(15));
                Require(result.ExitCode == 0 && result.ResourcesRecovered,
                    $"scheduled-font-{execution}-completed");
                Require(monitor.Complete(fontDispatch, result.Result),
                    $"scheduled-font-{execution}-reclaimed");
                RequireEditorUnchanged();
            }

            Require(errors.Count == 0 && !access.Gate.IsSameProcessReserved,
                "no-editor-error-and-gate-reusable");
            recording.SetSource("PRINT \"editor-after\"");
            Require(await recording.PlayAsync() == 0
                && access.OutputText.Contains("editor-after", StringComparison.Ordinal)
                && access.CanEnterInteractive,
                "normal-editor-playback-remains-usable");
            recording.DisableCopyAfterRecording();
            recording.ToggleRecording();
            await WaitFor(() => recording.HasRecorder && recording.IsRunning
                && !recording.IsStarting);
            await recording.FinishRecordingAsync();
            Require(!recording.HasRecorder && !recording.RecoveryPending
                && access.CanEnterInteractive && errors.Count == 0,
                "real-recording-reclaims-and-releases-interactive-gate");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                checks,
                fonts,
                completed = 7
            }), new UTF8Encoding(false));
            return 0;
        }
        finally
        {
            try
            {
                access.RequestPlaybackStop();
                if (running is not null)
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(15));
                }

                foreach (SchedulerDispatch dispatch in dispatched)
                {
                    monitor.Complete(dispatch, false, resourcesRecovered: access.CanStartInput);
                }

                monitor.StopAccepting();
                if (recording.HasRecorder)
                {
                    await recording.FinishRecordingAsync();
                }

                main.Close();
            }
            finally
            {
                if (previousProfile is null)
                {
                    File.Delete(profile);
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, previousProfile);
                }
            }
        }

        void Require(bool condition, string check)
            => VerificationAssert.Require(checks, condition, check);

        void RequireEditorUnchanged() => Require(
            access.DraftText == "未保存の編集原稿" && access.OutputText == "編集画面の出力"
                && recording.ScriptPath == Path.Combine(directory, "editor.uws")
                && main.FileLabel.Text == "編集の保存先" && access.IsModified,
            "editor-state-preserved-" + checks.Count);

        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}
