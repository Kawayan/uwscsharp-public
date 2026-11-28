using System.Windows;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// 記録の診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class RecordingVerificationAccess(MainWindow window)
    {
        internal static string SettingsPath => RecordingSettingsPath;

        internal string Source => window.Editor.Text;

        internal Button RecordButton => window.RecordButton;

        internal Button SaveButton => window.SaveButton;

        internal string Status => window.StatusLabel.Text;

        internal string? ScriptPath => window.scriptPath;

        internal bool IsModified => window.modified;

        internal bool HasRecorder => window.recording.HasRecorder;

        internal bool IsStarting => window.recording.IsStarting;

        internal bool IsRunning => window.recording.IsRunning;

        internal bool RecoveryPending => window.recording.RecoveryPending;

        internal bool CanEditRecordingSettings => window.WindowSettingsButton.IsEnabled;

        internal RecordingSettings Settings => window.recordingSettings;

        internal void SetCopyOption(bool enabled)
        {
            window.recordingSettings = window.recordingSettings with { CopyAfterRecording = enabled };
        }

        internal void DisableCopyAfterRecording() =>
            window.recordingSettings = window.recordingSettings with
            {
                CopyAfterRecording = false
            };

        internal Task EditRecordingSettingsAsync(Func<WindowSettingsWindow, Task> edit) =>
            SettingsVerification.RunSettingsModalAsync(window.Dispatcher,
                new SettingsVerificationAccess(window).ClickWindowSettings, edit,
                allowLoadError: false, "本体設定の読込み", "本体設定の処理が完了しませんでした。");

        internal void SaveScript(string path) => window.SaveScriptFile(path);

        internal void LoadScript(string path) => window.LoadScriptFile(path);

        internal void SetSource(string source) => window.Editor.Text = source;

        internal void DiscardDraft() => window.modified = false;

        internal void ToggleRecording() =>
            window.RecordClick(window, new RoutedEventArgs());

        internal Task FinishRecordingAsync() => window.FinishRecordingAsync();

        internal void ClickStop() =>
            window.StopClick(window, new RoutedEventArgs());

        internal Task<int> PlayAsync() => window.PlayAsync([]);

        internal void CancelPlayback() => window.cancellation!.Cancel();
    }
}
