using System.IO;

namespace UWSCSharp.Wpf;

internal static class HotkeySettingsUiVerification
{
    // 起動・保存の実経路へ偽の登録先を注入し、入力やOS登録なしで検査します。
    internal static void Run(MainWindow window, string directory,
        ICollection<string> checks, ICollection<Exception> errors)
    {
        var access = new MainWindow.HotkeySettingsVerificationAccess(window);
        string path = Path.Combine(directory, "hotkey-reservation-settings.json");
        var backend = new SettingsVerificationBackend();
        access.SetRegistration(backend);
        try
        {
            const string invalid = """{"RecordKey":120,"PauseKey":120}""";
            File.WriteAllText(path, invalid);
            access.ApplySettings(path);
            VerificationAssert.Require(window.HotkeyLabel.Text.Contains("既定値")
                && access.Settings == new HotkeySettings()
                && backend.Keys.Count == 4 && File.ReadAllText(path) == invalid,
                "構造上不正な保存設定の警告と復帰");
            checks.Add("hotkey-invalid-settings-startup-warning");

            access.DisposeRegistration();
            backend = new SettingsVerificationBackend();
            access.SetRegistration(backend);
            const string reserved = """{"Modifiers":7,"StopKey":122}""";
            File.WriteAllText(path, reserved);
            access.ApplySettings(path);
            VerificationAssert.Require(window.HotkeyLabel.Text.Contains("Ctrl+Alt+Shift+F11")
                && window.HotkeyLabel.Text.Contains("設定を変更")
                && access.Settings == new HotkeySettings(Modifiers: 7, StopKey: 122)
                && backend.Keys.Count == 0 && File.ReadAllText(path) == reserved,
                "専用予約の設定保持・登録拒否・警告");
            checks.Add("hotkey-reserved-settings-preserved-and-rejected");

            var corrected = new HotkeySettings(Modifiers: 15, StopKey: 122);
            using (var locked = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.None))
            {
                access.SaveSettings(corrected, path);
            }

            Console.WriteLine("hotkey-save-failure="
                + string.Join(",", errors.Select(error => error.GetType().FullName))
                + $"; restored-key-count={backend.Keys.Count}");
            VerificationAssert.Require(errors.Count == 1
                && (errors.Single() is IOException or UnauthorizedAccessException)
                && backend.Keys.Count == 0 && access.Settings.HasReservedShortcut
                && File.ReadAllText(path) == reserved,
                "保存失敗時の未登録への復帰と元設定の保持");
            access.SaveSettings(corrected, path);
            VerificationAssert.Require(backend.Keys.Count == 4
                && access.Settings == corrected && HotkeySettings.Load(path) == corrected,
                "明示訂正後の登録と保存");
            checks.Add("hotkey-reserved-settings-save-failure-and-correction");
        }
        finally
        {
            access.DisposeRegistration();
            access.ClearRegistration();
            File.Delete(path);
        }
    }

    private sealed class SettingsVerificationBackend : IHotkeyBackend
    {
        internal Dictionary<int, int> Keys { get; } = [];

        public bool Register(int id, uint modifiers, int key)
        {
            Keys.Add(id, key);
            return true;
        }

        public bool Unregister(int id) => Keys.Remove(id);
    }
}
