using System.Windows;
using System.Windows.Threading;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private static readonly TimeSpan InputCleanupPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly WindowsSelectiveInputCleanup selectiveInputCleanup = new();
    private readonly InputCleanupController inputCleanup;
    private readonly Func<bool>? pendingCleanupExitConfirmation;
    private readonly DispatcherTimer inputCleanupTimer = new()
    {
        Interval = InputCleanupPollInterval
    };
    private bool inputCleanupWasNeeded;
    private bool inputCleanupClosed;

    private void InitializeInputCleanup()
    {
        inputCleanupTimer.Tick += async (_, _) =>
        {
            if (!inputCleanup.IsBusy && cancellation is null && !recording.HasRecorder
                && !recording.IsStarting && !recording.IsStopping
                && scheduledPlayback is null)
            {
                await RefreshInputCleanupAsync();
            }
        };
        Closed += (_, _) =>
        {
            inputCleanupClosed = true;
            inputCleanupTimer.Stop();
        };
        RefreshInputCleanupControls();
    }

    internal Task RefreshInputCleanupAsync() => PerformInputCleanupAsync(retryRequested: false);

    private async void InputCleanupClick(object sender, RoutedEventArgs e)
    {
        if (inputCleanup.CanRetry && cancellation is null && !recording.HasRecorder
            && !recording.IsStarting && !recording.IsStopping
            && scheduledPlayback is null)
        {
            await PerformInputCleanupAsync(retryRequested: true);
        }
    }

    private async Task PerformInputCleanupAsync(bool retryRequested)
    {
        Dispatcher.VerifyAccess();
        Task<InputCleanupSnapshot> operation = retryRequested
            ? inputCleanup.RetryAsync() : inputCleanup.RefreshAsync();
        RefreshInputCleanupControls();
        InputCleanupSnapshot result = await operation;
        if (inputCleanupClosed)
        {
            return;
        }

        foreach (Exception error in result.Errors)
        {
            string diagnostic = "入力フックの回収: " + ScriptLogHost.FormatDiagnostic(error);
            if (scheduledPlayback is not null)
            {
                scheduledPlayback.Output.Append(diagnostic);
            }
            else
            {
                AppendOutput(OutputText.Join([diagnostic]));
            }
        }

        RefreshInputCleanupControls();
    }

    private void RefreshInputCleanupControls()
    {
        schedulerGate.SetInputCleanupConfirmed(inputCleanup.CanStart);
        var actions = MainWindowActionState.Create(
            cancellation is not null, recording.IsStarting, recording.IsStopping,
            schedulerGate.IsSameProcessReserved, SchedulerLifetimeBusy,
            recording.HasRecorder, recording.UiState.CanRecord,
            schedulerGate.CanEnterInteractive, inputCleanup.CanRetry);
        PlayButton.IsEnabled = actions.CanPlay;
        RecordButton.IsEnabled = actions.CanRecord;
        InputCleanupButton.IsEnabled = actions.CanRetryCleanup;
        inputCleanupWasNeeded |= !inputCleanup.HasKnownCount || inputCleanup.PendingCount > 0;
        InputCleanupLabel.Visibility = inputCleanupWasNeeded ? Visibility.Visible : Visibility.Collapsed;
        if (inputCleanupWasNeeded)
        {
            InputCleanupLabel.Text = inputCleanup.IsBusy
                ? "入力フックの回収状態を確認・再回収しています"
                : !inputCleanup.HasKnownCount
                ? "入力フックの回収状態を確認できません。再生・記録の開始を制限しています"
                : inputCleanup.PendingCount == 0
                ? "未回収所有者: 0（回収完了。過去の診断は実行結果に残ります）"
                : $"未回収所有者: {inputCleanup.PendingCount}。再生・記録を開始する前に再回収してください";
        }

        // 完了の照会だけを繰り返し、自動で再回収を要求しません。
        inputCleanupTimer.IsEnabled = !inputCleanupClosed && inputCleanup.HasKnownCount
            && inputCleanup.PendingCount > 0;
        if (scheduledPlayback is not null)
        {
            PauseButton.IsEnabled = scheduledPlayback.View.CanPause;
            PauseButton.Content = scheduledPlayback.View.PauseCaption;
            StopButton.IsEnabled = scheduledPlayback.View.CanStop;
        }
    }

    private string InputCleanupExitDiagnostic() => inputCleanup.HasKnownCount
        ? $"未回収所有者: {inputCleanup.PendingCount}。プロセス終了は明示解除の成功を示しません。"
        : "入力フックの回収状態を確認できません。プロセス終了は明示解除の成功を示しません。";

    private bool ConfirmInputCleanupExit() => pendingCleanupExitConfirmation?.Invoke()
        ?? MessageBox.Show(this, InputCleanupExitDiagnostic()
            + "\n閉じる場合は「はい」、画面へ戻って再回収する場合は「いいえ」を選んでください。",
            ApplicationInfo.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
