using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// スクリプトのホットキー診断で操作用の登録と再生状態を扱います。
    /// </summary>
    internal sealed class ScriptHotkeyVerificationAccess(MainWindow window)
    {
        internal static bool RegisterThreadHotkey(int id, uint modifiers, uint key) =>
            RegisterHotKey(0, id, modifiers, key);

        internal static bool UnregisterThreadHotkey(int id) =>
            UnregisterHotKey(0, id);

        internal HotkeySettings Settings => window.hotkeySettings;

        internal int[] ActiveKeys => window.hotkeys!.ActiveKeys;

        internal bool IsPlaying => window.cancellation is not null;

        internal bool ApplyHotkeys(HotkeySettings settings) =>
            window.hotkeys is { } registration && registration.Apply(settings);

        internal bool IsHotkeyRegistered(int id) =>
            window.hotkeys!.IsRegistered(id);

        internal void SetCancellation(CancellationTokenSource? source) =>
            window.cancellation = source;

        internal IScriptStopFormOptionsSession CreateStopOptions(
            CancellationToken token) => window.CreateStopOptions(token);

        internal Task SendWithoutKeyboardHotkeysAsync(int key,
            Func<Task> send) => window.SendWithoutKeyboardHotkeysAsync(key, send);
    }
}
