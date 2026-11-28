using System.IO;
using System.Text.Json;
using System.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || recording.IsStarting || recording.IsStopping
            || schedulerGate.IsSameProcessReserved)
        {
            return;
        }

        if (recording.HasRecorder)
        {
            await FinishRecordingAsync();
            return;
        }

        if (!inputCleanup.CanStart)
        {
            return;
        }

        if (!ConfirmDiscard())
        {
            return;
        }

        schedulerGate.SetInputCleanupConfirmed(inputCleanup.CanStart);
        recordingActivity = schedulerGate.TryEnterInteractive();
        if (recordingActivity is null)
        {
            return;
        }

        Task starting = recording.StartAsync(recordingSettings.CopyAfterRecording);
        RefreshRecordingUi();
        StatusLabel.Text = "記録を開始しています";
        try
        {
            await starting;
            StatusLabel.Text = "記録中";
        }
        catch (Exception error)
        {
            RefreshRecordingUi();
            ShowError(error);
        }
        finally
        {
            RefreshRecordingUi();
        }
    }

    private async Task FinishRecordingAsync(
        RecordingStopReason reason = RecordingStopReason.Manual)
    {
        if (!recording.HasRecorder || recording.IsStarting || recording.IsStopping)
        {
            return;
        }

        Task<RecordingResult?> stopping = recording.StopAsync(reason);
        RefreshRecordingUi();
        StatusLabel.Text = "記録を終了しています";
        RecordingResult? result;
        try
        {
            result = await stopping;
            if (result is not { } completed)
            {
                return;
            }

            EditorPanel.IsExpanded = true;
            Editor.Text = completed.Source;
            scriptPath = null;
            FileLabel.Text = recordingSettings.RelativeMouseCoordinates
                ? "新規記録（相対座標・仮想キー）"
                : "新規記録（絶対座標・仮想キー）";
            StatusLabel.Text = completed.Overflowed
                ? "記録終了：50,000件の上限で入力が欠落しました"
                : "記録終了（試作：再生前に内容を確認してください）";
            if (!string.IsNullOrEmpty(completed.Warning))
            {
                StatusLabel.Text += "：" + completed.Warning;
                AppendOutput(OutputText.Join([completed.Warning]));
            }
        }
        catch (Exception error)
        {
            // 失敗時は旧ソース・ファイル名・未保存状態を変更しません。
            RefreshRecordingUi();
            ShowError(error);
            return;
        }
        finally
        {
            RefreshRecordingUi();
        }

        // 記録結果の反映後に行い、コピー失敗を記録生成の失敗と混同しません。
        if (result is { CopyToClipboard: true } ready)
        {
            try
            {
                copyRecordingText(ready.Source);
            }
            catch (Exception error)
            {
                ShowError(error);
                StatusLabel.Text = "記録終了：クリップボードへコピーできませんでした。編集欄の記録は残っています。";
            }
        }
    }

    private void RefreshRecordingUi()
    {
        if (!recording.HasRecorder && !recording.IsStarting && !recording.IsStopping)
        {
            recordingActivity?.Dispose();
            recordingActivity = null;
        }

        RecordingUiState state = recording.UiState;
        RecordButton.IsEnabled = state.CanRecord;
        RecordButton.Content = state.RecordLabel;
        StopButton.IsEnabled = state.CanStop;
        SettingsButton.IsEnabled = state.CanEdit;
        LogSettingsButton.IsEnabled = state.CanEdit;
        WindowSettingsButton.IsEnabled = state.CanEdit;
        AssociationButton.IsEnabled = state.CanEdit;
        HelpButton.IsEnabled = state.CanEdit;
        LoadButton.IsEnabled = state.CanEdit;
        SaveButton.IsEnabled = state.CanEdit;
        PlayButton.IsEnabled = state.CanEdit;
        Editor.IsReadOnly = !state.CanEdit;
        recordTimer.IsEnabled = state.Monitor;
        RefreshInputCleanupControls();
    }

    private void InitializeRecordingSettings(object? sender, EventArgs e)
    {
        try
        {
            recordingSettings = RecordingSettings.Load(RecordingSettingsPath);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            StatusLabel.Text = "記録設定を読めないため既定値を使用します。";
        }
    }
}
