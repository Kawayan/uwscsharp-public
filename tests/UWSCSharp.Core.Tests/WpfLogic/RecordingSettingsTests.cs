using System.Text.Json;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class RecordingSettingsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecordingOptionsRoundTripIndependently(bool compact, bool relative)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.json");
            var settings = new RecordingSettings(compact, relative,
                RecordButtonClicks: true, BackgroundPlayback: !relative);
            settings.Save(path);
            Assert.Equal(settings, RecordingSettings.Load(path));
            File.WriteAllText(path, "{\"OmitIdleTimeAndMouseMoves\":true}");
            Assert.Equal(new RecordingSettings(true, false), RecordingSettings.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileAndMissingPropertyDefaultToOff()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.json");
            Assert.False(RecordingSettings.Load(path).OmitIdleTimeAndMouseMoves);
            File.WriteAllText(path, "{}");
            Assert.False(RecordingSettings.Load(path).OmitIdleTimeAndMouseMoves);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavesOnAndThenOffWithoutChangingHotkeys()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings", "recording.json");
            string hotkeys = Path.Combine(directory, "settings", "hotkeys.json");
            new HotkeySettings(Modifiers: 2).Save(hotkeys);
            string original = File.ReadAllText(hotkeys);
            new RecordingSettings(true).Save(path);
            Assert.True(RecordingSettings.Load(path).OmitIdleTimeAndMouseMoves);
            new RecordingSettings(false).Save(path);
            Assert.False(RecordingSettings.Load(path).OmitIdleTimeAndMouseMoves);
            Assert.Equal(original, File.ReadAllText(hotkeys));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"OmitIdleTimeAndMouseMoves\":\"true\"}")]
    public void InvalidJsonIsReported(string json)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.json");
            File.WriteAllText(path, json);
            Assert.Throws<JsonException>(() => RecordingSettings.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NullSettingsAreReported()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.json");
            File.WriteAllText(path, "null");
            Assert.Throws<InvalidDataException>(() => RecordingSettings.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedReplacementPreservesSavedValueAndRemovesTemporaryFile()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.json");
            new RecordingSettings(true).Save(path);
            using (var locked = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => new RecordingSettings(false).Save(path));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }

            Assert.True(RecordingSettings.Load(path).OmitIdleTimeAndMouseMoves);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "uwsc-recording-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
