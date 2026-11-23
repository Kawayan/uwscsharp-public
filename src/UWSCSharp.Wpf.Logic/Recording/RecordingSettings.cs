using System.IO;
using System.Text.Json;

namespace UWSCSharp.Wpf;

// 記録の省略設定を、ホットキー設定とは独立して保存します。
internal sealed record RecordingSettings(
    bool OmitIdleTimeAndMouseMoves = false,
    bool RelativeMouseCoordinates = false,
    bool CopyAfterRecording = false,
    bool RecordButtonClicks = false,
    bool BackgroundPlayback = false)
{
    internal static RecordingSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new();
        }

        return JsonSerializer.Deserialize<RecordingSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("記録設定が空です。");
    }

    internal void Save(string path)
    {
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
}
