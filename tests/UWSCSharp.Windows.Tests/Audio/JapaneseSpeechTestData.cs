using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 同じエンジン版のローカル検証資材だけを、破棄されるゲストへ補います。
internal static class JapaneseSpeechTestData
{
    internal static void Prepare()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string source = Path.Combine(AppContext.BaseDirectory, "SpeechData", "ja-JP");
        string manifestPath = Path.Combine(source, "manifest.json");
        Assert.True(File.Exists(manifestPath), "日本語音声の検証資材が配置されていません。");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string engine = Path.Combine(windows, "System32", "speech_onecore", "engines", "tts", "MSTTSEngine_OneCore.dll");
        Assert.Equal(manifest.RootElement.GetProperty("engineVersion").GetString(),
            FileVersionInfo.GetVersionInfo(engine).FileVersion);
        string destination = Path.Combine(windows, "Speech_OneCore", "Engines", "TTS", "ja-JP");
        var files = new List<(string Source, string Destination)>();
        foreach (JsonElement file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("name").GetString()!;
            Assert.Equal(Path.GetFileName(name), name);
            Assert.DoesNotContain("..", name);
            string input = Path.Combine(source, name);
            string target = Path.Combine(destination, name);
            string hash = file.GetProperty("sha256").GetString()!;
            Assert.Equal(hash, Hash(input));
            if (File.Exists(target))
            {
                Assert.Equal(hash, Hash(target));
            }
            else
            {
                files.Add((input, target));
            }
        }

        Directory.CreateDirectory(destination);
        foreach ((string input, string target) in files)
        {
            File.Copy(input, target, overwrite: false);
        }

        File.WriteAllText(Path.Combine(@"C:\ui-output",
            $"speech-data-preparation-{Guid.NewGuid():N}.json"),
            JsonSerializer.Serialize(new
            {
                destination,
                copiedFiles = files.Count,
                manifest = manifest.RootElement
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
