using System.Windows;
using System.Windows.Controls;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// ランチの診断で参照する画面の状態と操作を提供します。
    /// </summary>
    internal sealed class LauncherVerificationAccess(MainWindow window)
    {
        internal string Source => window.Editor.Text;

        internal string Output => window.Output.Text;

        internal string FileLabel => window.FileLabel.Text;

        internal string Status => window.StatusLabel.Text;

        internal string? ScriptPath => window.scriptPath;

        internal bool IsModified => window.modified;

        internal bool IsReadOnly => window.Editor.IsReadOnly;

        internal LauncherSettings? Settings => window.launcher.Current;

        internal ContextMenu? Menu => window.launcherMenu;

        internal Exception? SettingsError => window.launcher.LoadError;

        internal string Notice => window.LauncherNotice.Text;

        internal string HotkeyStatus => window.HotkeyLabel.Text;

        internal string HotkeyNotice => window.LauncherHotkeyNotice.Text;

        internal string? PositionSettingsPath =>
            window.launcherPositionSettingsPath;

        internal bool CanEditSettings => window.CanEditLauncher;

        internal bool CanOpenMenu => window.LauncherButton.IsEnabled;

        internal bool UsesButtonPlacement =>
            window.launcherMenu!.PlacementTarget == window.LauncherButton;

        internal bool IsClosed => window.launcherClosed;

        internal int PendingHotkeys =>
            window.launcherHotkeys!.PendingBindings.Length;

        internal ScriptHotkey? ActiveHotkey =>
            window.launcherHotkeys!.ActiveShortcut;

        internal bool IsPlaying => window.cancellation is not null;

        internal bool HasRecorder => window.recording.HasRecorder;

        internal bool IsRecording => window.recording.IsRunning;

        internal bool IsStartingRecording => window.recording.IsStarting;

        internal bool RecordingRecoveryPending =>
            window.recording.RecoveryPending;

        internal void ConfigureHotkey(IHotkeyBackend backend, string path)
        {
            window.launcherHotkeyBackendOverride = backend;
            window.launcherPositionSettingsPath = path;
        }

        internal void OpenMenu(bool atPointer = false) =>
            window.OpenLauncher(atPointer);

        internal void EditSettings() => window.EditLauncher();

        internal void EditPosition() => window.EditLauncherPosition();

        internal void ReloadSettings() => window.ReloadLauncher();

        internal void ResetSettings() => window.ResetLauncher();

        internal void OverrideLaunch(Action<LauncherCommand>? launch) =>
            window.launchCommandOverride = launch;

        internal Task<int?> LaunchAsync(LauncherCommand command) =>
            window.LaunchFromMenuAsync(command);

        internal bool AcceptsHotkey(int id) =>
            window.launcherHotkeys!.Accepts(id);

        internal void DisposeHotkeys() => window.launcherHotkeys?.Dispose();

        internal void SaveMainHotkeySettings(HotkeySettings settings,
            string path) => window.SaveHotkeySettings(settings, path);

        internal void DispatchHotkey(int id, ref bool handled) =>
            window.HotkeyMessage(window.windowHandle, 0x312, id, 0,
                ref handled);

        internal void ApplyHotkeySettings(LauncherSettings settings) =>
            window.launcherHotkeys!.Apply(settings, window.MainShortcuts(),
                () => window.launcher.Save(settings));

        internal void RefreshHotkeyNotice() =>
            window.RefreshLauncherHotkeyNotice();

        internal Task OpenFromHotkeyAsync(int id) =>
            window.OpenLauncherFromHotkeyAsync(id);

        internal void DisableRecordingCopy() =>
            window.recordingSettings = window.recordingSettings with
            {
                CopyAfterRecording = false
            };

        internal void SetScript(string path, string source)
        {
            window.scriptPath = path;
            window.Editor.Text = source;
        }

        internal Task<int> PlayAsync() => window.PlayAsync([]);

        internal void CancelPlayback() => window.cancellation?.Cancel();

        internal void StartRecording() =>
            window.RecordClick(window, new RoutedEventArgs());

        internal Task FinishRecordingAsync() =>
            window.FinishRecordingAsync();

        internal void SetSource(string source) => window.Editor.Text = source;

        internal void SetOutput(string output) => window.Output.Text = output;

        internal void SaveSettings(LauncherSettings settings) =>
            window.launcher.Save(settings);

        internal void ClickGroupChild(int index)
        {
            window.OpenLauncher();
            var group = (MenuItem)window.launcherMenu!.Items[0];
            group.IsSubmenuOpen = true;
            var item = (MenuItem)group.Items[index];
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            window.launcherMenu.IsOpen = false;
        }
    }
}
