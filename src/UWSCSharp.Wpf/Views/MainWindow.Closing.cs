using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private ProcessStartInfo? pendingRestart;
    private int restartExitCode;
    private bool confirmingClose;

    private static ProcessStartInfo PrepareApplicationRestart(
        PowerCommandRequest request, string? path,
        IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (request.Command != PowerCommand.RestartApplication)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: WPFではP_UWSC_REEXECを指定してください。");
        }

        var script = new ScriptRunOptions(arguments) { SourcePath = path };
        var host = WindowsCommandHost.ForCurrentProcess();
        if (!request.RestartScript && string.Equals(Path.GetFileName(host.Executable),
            "UWSCSharp.Association.exe", StringComparison.OrdinalIgnoreCase))
        {
            host = new WindowsCommandHost(Path.Combine(
                AppContext.BaseDirectory, "UWSCSharp.Wpf.exe"));
        }

        return WindowsApplicationRestart.CreateStartInfo(
            host, workingDirectory, request.RestartScript, script, []);
    }

    private void StartReplacementAfterClose(ProcessStartInfo start)
    {
        try
        {
            using var replacement = Process.Start(start);
            if (replacement is null)
            {
                throw new InvalidOperationException("再起動するプロセスを起動できません。");
            }
        }
        catch (Exception error) when (error is Win32Exception
            or InvalidOperationException or IOException)
        {
            restartExitCode = 1;
            if (commandLine)
            {
                App.ReportCommandLineError(error);
            }
            else
            {
                MessageBox.Show(error.Message, ApplicationInfo.Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CancelPendingRestart()
    {
        if (pendingRestart is not null)
        {
            pendingRestart = null;
            restartExitCode = 1;
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (pendingRestart is not null
            && (!inputCleanup.CanStart || inputCleanup.IsBusy
                || launcherHotkeys?.IsBusy == true || recording.HasRecorder
                || recording.IsStarting || recording.IsStopping
                || cancellation is not null || scheduledPlayback is not null
                || SchedulerLifetimeBusy || schedulerExitHeld))
        {
            CancelPendingRestart();
            StatusLabel.Text = "回収を確認できないため再起動を取り消しました";
            e.Cancel = true;
            return;
        }

        if (schedulerHost is not null && !schedulerCloseReady)
        {
            e.Cancel = true;
            await PrepareSchedulerCloseAsync();
            if (!schedulerCloseReady)
            {
                CancelPendingRestart();
                closingFromTray = false;
            }
            return;
        }

        if (scheduledPlayback is not null)
        {
            closingFromTray = false;
            scheduledPlayback.RequestStop();
            e.Cancel = true;
            return;
        }

        if (inputCleanup.IsBusy || launcherHotkeys?.IsBusy == true)
        {
            closingFromTray = false;
            e.Cancel = true;
            StatusLabel.Text = "回収状態の確認・再回収が終わってから、もう一度閉じてください";
            return;
        }

        if (recording.IsStarting || recording.IsStopping)
        {
            closingFromTray = false;
            e.Cancel = true;
            return;
        }

        if (recording.HasRecorder)
        {
            e.Cancel = true;
            await FinishRecordingAsync(RecordingStopReason.OwnerClosing);
            if (!recording.HasRecorder)
            {
                // 元のClosingを返した後で、未保存確認を含む終了を再実行します。
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            else
            {
                closingFromTray = false;
            }

            return;
        }

        if (cancellation is not null)
        {
            closingFromTray = false;
            RequestPlaybackStop();
            StatusLabel.Text = "停止後にもう一度閉じてください";
            e.Cancel = true;
            return;
        }

        if (!commandLine && !inputCleanup.CanStart && !ConfirmInputCleanupExit())
        {
            closingFromTray = false;
            schedulerCloseReady = false;
            e.Cancel = true;
            return;
        }

        confirmingClose = true;
        try
        {
            e.Cancel = !ConfirmDiscard();
        }
        finally
        {
            confirmingClose = false;
        }
        if (e.Cancel)
        {
            closingFromTray = false;
            schedulerCloseReady = false;
            CancelPendingRestart();
        }
        else if (launcherHotkeys is not null)
        {
            launcherHotkeys.Dispose();
            RefreshLauncherHotkeyNotice();
            if (launcherHotkeys.PendingBindings.Length != 0)
            {
                CancelPendingRestart();
                const string message = "ランチ用ホットキーの回収が完了していません。"
                    + "本体は終了しますが、明示的な解除の成功は確認できていません。";
                if (errorReporter is not null)
                {
                    errorReporter(new InvalidOperationException(message));
                }
                else
                {
                    System.Windows.MessageBox.Show(this, message, ApplicationInfo.Title,
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
        }

        if (!e.Cancel)
        {
            SaveTrayStartupState();
        }

        if (!e.Cancel && pendingRestart is { } restart)
        {
            pendingRestart = null;
            Closed += (_, _) => StartReplacementAfterClose(restart);
        }
    }
}
