using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// 再生の診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class PlaybackVerificationAccess(MainWindow window)
    {
        internal string Output => window.Output.Text;

        internal bool IsPlaying => window.cancellation is not null;

        internal bool IsPaused => window.pause?.IsPaused == true;

        internal StopWindow? StopWindow => window.stopWindow;

        internal IScriptStopFormOptionsSession? ActiveStopOptions =>
            window.activeStopOptions;

        internal bool StopOptionsApplied =>
            window.activeStopOptions is { TopChanged: true, HotkeyChanged: true };

        internal bool ApplyHotkeys(HotkeySettings settings) =>
            window.hotkeys is { } registration && registration.Apply(settings);

        internal bool IsHotkeyRegistered(int id) =>
            window.hotkeys!.IsRegistered(id);

        internal LogWindow GetOrCreateLogWindow() =>
            window.logWindow ??= window.CreateLogWindow();

        internal void SetLogView(ScriptLogView view) => window.logView = view;

        internal int PendingSelectiveInputCleanup =>
            window.selectiveInputCleanup.PendingCount;

        internal bool CanStartInput => window.inputCleanup.CanStart;

        internal StopWindow EnsureStopWindow() => window.EnsureStopWindow();

        internal void RequestStop() => window.RequestPlaybackStop();
    }
}
