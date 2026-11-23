using System.IO;
using System.Text.Json;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal sealed record HotkeySettings(
    bool Enabled = true, uint Modifiers = 1,
    int RecordKey = 119, int PauseKey = 120, int StopKey = 121,
    int PlayKey = 118)
{
    internal int[] Keys => [RecordKey, PauseKey, StopKey, PlayKey];

    internal bool HasReservedShortcut => Keys.Any(key =>
        SelectiveInputEmergencyShortcut.IsReserved(new(key, (int)Modifiers)));

    internal static string ReservationWarning =>
        SelectiveInputEmergencyShortcut.DisplayName
        + "は緊急解除専用です。ホットキー設定を変更してください。";

    internal void Validate()
    {
        ValidateKeys();
        if (HasReservedShortcut)
        {
            throw new InvalidDataException(ReservationWarning);
        }
    }

    private void ValidateKeys()
    {
        if ((Modifiers & ~15u) != 0 || Keys.Any(key => key is < 112 or > 122)
            || Keys.Distinct().Count() != 4)
        {
            throw new InvalidDataException("ホットキーは異なるF1〜F11を指定してください。");
        }
    }

    internal static HotkeySettings Load(string path) => LoadCore(path, false);

    // 起動時は競合するキーを残し、登録側で拒否して設定変更を促します。
    internal static HotkeySettings LoadForStartup(string path) => LoadCore(path, true);

    private static HotkeySettings LoadCore(string path, bool preserveReserved)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        string json = File.ReadAllText(path);
        HotkeySettings settings = JsonSerializer.Deserialize<HotkeySettings>(
            json) ?? throw new InvalidDataException("設定が空です。");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(PlayKey), out _)
            && new[] { settings.RecordKey, settings.PauseKey, settings.StopKey }.Contains(settings.PlayKey))
        {
            // 旧設定の操作キーを保持し、再生開始だけ空いているキーへ移行します。
            settings = settings with
            {
                PlayKey = Enumerable.Range(112, 11).First(key =>
                    key != settings.RecordKey && key != settings.PauseKey && key != settings.StopKey)
            };
        }
        settings.ValidateKeys();
        if (!preserveReserved)
        {
            settings.Validate();
        }

        return settings;
    }

    internal void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal string Describe() => !Enabled ? "ホットキー無効" :
        $"再生 {Chord(PlayKey)} / 記録 {Chord(RecordKey)} / 一時停止 {Chord(PauseKey)} / 停止 {Chord(StopKey)}";

    private string Chord(int key) =>
        ((Modifiers & 2) != 0 ? "Ctrl+" : "")
        + ((Modifiers & 1) != 0 ? "Alt+" : "")
        + ((Modifiers & 4) != 0 ? "Shift+" : "")
        + ((Modifiers & 8) != 0 ? "Win+" : "")
        + $"F{key - 111}";
}
