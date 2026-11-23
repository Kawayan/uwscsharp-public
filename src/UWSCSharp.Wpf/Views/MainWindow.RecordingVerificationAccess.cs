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

        internal bool? CopyOptionChecked =>
            window.CopyRecordingCheckBox.IsChecked;

        internal bool CopyOptionEnabled =>
            window.CopyRecordingCheckBox.IsEnabled;

        internal bool CopyAfterRecording =>
            window.recordingSettings.CopyAfterRecording;

        internal void SetCopyOption(bool enabled)
        {
            window.recordingSettings = new(CopyAfterRecording: enabled);
            window.CopyRecordingCheckBox.IsChecked = enabled;
        }

        internal void DisableCopyAfterRecording() =>
            window.recordingSettings = window.recordingSettings with
            {
                CopyAfterRecording = false
            };

        internal bool HighLevelOptionChecked =>
            window.HighLevelRecordingCheckBox.IsChecked == true;

        internal void ClickHighLevelOption()
        {
            window.HighLevelRecordingCheckBox.IsChecked = true;
            window.HighLevelRecordingCheckBox.RaiseEvent(
                new RoutedEventArgs(CheckBox.ClickEvent));
        }

        internal bool BackgroundOptionChecked =>
            window.BackgroundRecordingCheckBox.IsChecked == true;

        internal bool OmitIdleOptionChecked =>
            window.OmitIdleRecordingCheckBox.IsChecked == true;

        internal void ClickOmitIdleOption()
        {
            window.OmitIdleRecordingCheckBox.IsChecked = true;
            window.OmitIdleRecordingCheckBox.RaiseEvent(
                new RoutedEventArgs(CheckBox.ClickEvent));
        }

        internal bool RelativeMouseOptionChecked =>
            window.RelativeMouseRecordingCheckBox.IsChecked == true;

        internal void ClickRelativeMouseOption()
        {
            window.RelativeMouseRecordingCheckBox.IsChecked = true;
            window.RelativeMouseRecordingCheckBox.RaiseEvent(
                new RoutedEventArgs(CheckBox.ClickEvent));
        }

        internal bool BackgroundOptionEnabled =>
            window.BackgroundRecordingCheckBox.IsEnabled;

        internal void ClickBackgroundOption(bool enabled)
        {
            window.BackgroundRecordingCheckBox.IsChecked = enabled;
            window.BackgroundRecordingCheckBox.RaiseEvent(
                new RoutedEventArgs(CheckBox.ClickEvent));
        }

        internal void ClickCopyOption(bool enabled)
        {
            window.CopyRecordingCheckBox.IsChecked = enabled;
            window.CopyRecordingCheckBox.RaiseEvent(
                new RoutedEventArgs(CheckBox.ClickEvent));
        }

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
