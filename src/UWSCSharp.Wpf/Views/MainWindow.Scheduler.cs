using System.IO;
using System.Windows;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal sealed record SchedulerPlaybackResult(int ExitCode,
    bool ResourcesRecovered, string Output, long OmittedCodeUnits,
    string? Diagnostic, bool CancellationCompleted)
{
    internal SchedulerExecutionResult Result => SchedulerExecutionResult.Completed(
        ExitCode, started: true, CancellationCompleted, ResourcesRecovered, Diagnostic);
}

public partial class MainWindow
{
    private readonly SchedulerExecutionGate schedulerGate = new();
    private SchedulerScriptPlayback? scheduledPlayback;
    private IDisposable? recordingActivity;

    internal async Task<SchedulerPlaybackResult> RunScheduledPlaybackAsync(
        SchedulerDispatch dispatch, string logDirectory,
        CancellationToken cancellationToken = default, Action? onStarted = null)
    {
        Dispatcher.VerifyAccess();
        if (dispatch.Request.Entry.Mode != SchedulerExecutionMode.SameProcess
            || !schedulerGate.IsSameProcessReserved || scheduledPlayback is not null
            || cancellation is not null || recording.HasRecorder
            || recording.IsStarting || recording.IsStopping || !inputCleanup.CanStart)
        {
            throw new InvalidOperationException("予約の同一プロセス実行を開始できません。");
        }

        var storage = new SchedulerManagedStorage(logDirectory);
        var execution = new SchedulerScriptPlayback(new MainSchedulerPlaybackHost(this),
            dispatch.Request.Entry.Path, dispatch.Request.Entry.Arguments,
            storage.LogPath(dispatch.Request.OccurrenceId), storage, dispatch.Request.WindowTrigger);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(scheduledPlayback, execution))
                {
                    execution.RequestStop();
                }
            }));
        scheduledPlayback = execution;
        RefreshInputCleanupControls();
        int code = 1;
        bool cancellationCompleted = false;
        string? diagnostic = null;
        try
        {
            onStarted?.Invoke();
            SchedulerScriptPlaybackResult result = await execution.RunAsync();
            code = result.ExitCode;
            cancellationCompleted = result.CancellationCompleted;
            diagnostic = result.Diagnostic;
        }
        finally
        {
            try
            {
                await RefreshInputCleanupAsync();
            }
            finally
            {
                try
                {
                    execution.Dispose();
                }
                finally
                {
                    scheduledPlayback = null;
                    RefreshInputCleanupControls();
                    PauseButton.IsEnabled = false;
                    PauseButton.Content = "Ⅱ 一時停止";
                    StopButton.IsEnabled = false;
                }
            }
        }

        return new SchedulerPlaybackResult(code, inputCleanup.CanStart,
            execution.Output.Render(), execution.Output.OmittedCodeUnits, diagnostic,
            cancellationCompleted);
    }

    private Task RunWorkerKeyboardAsync(int key, Func<Task> operation,
        CancellationToken cancellationToken) => RunKeyboardOperationCoreAsync(() =>
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (hotkeys is null)
                    {
                        await SendWithLauncherHotkeysAsync(key, operation);
                    }
                    else
                    {
                        await hotkeys.SendKeyboardInputAsync(key,
                            () => SendWithLauncherHotkeysAsync(key, operation));
                    }
                }
                finally
                {
                    if (hotkeys is not null)
                    {
                        HotkeyLabel.Text = hotkeys.Status;
                    }
                    RefreshLauncherHotkeyNotice();
                }
            }).Task.Unwrap(), cancellationToken);

    private sealed class MainSchedulerPlaybackHost(MainWindow main) : SchedulerPlaybackHost
    {
        internal override Window Window => main;

        internal override WindowsSelectiveInputCleanup Cleanup => main.selectiveInputCleanup;

        internal override HotkeyRegistration? Hotkeys => main.hotkeys;

        internal override Task SendWithoutKeyboardHotkeysAsync(int key, Func<Task> send) =>
            main.SendWithoutKeyboardHotkeysAsync(key, send);

        internal override Task RunKeyboardOperationAsync(Func<Task> operation) =>
            main.RunKeyboardOperationAsync(operation);

        internal override void RefreshControls() => main.RefreshInputCleanupControls();

        internal override void ExecuteSystemPowerCommand(
            PowerCommandRequest request) =>
            main.executeSystemPowerCommand(request);

        internal override bool IsCurrent(SchedulerScriptPlayback execution) =>
            ReferenceEquals(main.scheduledPlayback, execution);
    }
}
