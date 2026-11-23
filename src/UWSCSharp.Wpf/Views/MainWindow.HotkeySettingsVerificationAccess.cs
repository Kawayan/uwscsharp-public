namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// ホットキー設定の診断で登録先を差し替え、設定の読込みと保存を実行します。
    /// </summary>
    internal sealed class HotkeySettingsVerificationAccess(MainWindow window)
    {
        internal HotkeySettings Settings => window.hotkeySettings;

        internal void SetRegistration(IHotkeyBackend backend) =>
            window.hotkeys = new HotkeyRegistration(backend);

        internal void DisposeRegistration() => window.hotkeys!.Dispose();

        internal void ClearRegistration() => window.hotkeys = null;

        internal void ApplySettings(string path) =>
            window.ApplyHotkeySettings(path);

        internal void SaveSettings(HotkeySettings settings, string path) =>
            window.SaveHotkeySettings(settings, path);
    }
}
