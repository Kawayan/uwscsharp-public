using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class RecordingClipboardSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"OmitIdleTimeAndMouseMoves\":true}")]
    [InlineData("{\"OmitIdleTimeAndMouseMoves\":false,\"RelativeMouseCoordinates\":true}")]
    public void ExistingJsonWithoutCopyOptionKeepsItDisabled(string json)
    {
        WithFile(path =>
        {
            Assert.False(RecordingSettings.Load(path).CopyAfterRecording);
            File.WriteAllText(path, json);
            Assert.False(RecordingSettings.Load(path).CopyAfterRecording);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CopyOptionCanBeEnabledAndDisabledWithoutChangingOtherSettings(bool compact, bool relative)
    {
        WithFile(path =>
        {
            var on = new RecordingSettings(compact, relative, true);
            on.Save(path);
            Assert.Equal(on, RecordingSettings.Load(path));
            RecordingSettings off = on with
            {
                CopyAfterRecording = false
            };
            off.Save(path);
            Assert.Equal(off, RecordingSettings.Load(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        });
    }

    [Fact]
    public void FailedSavePreservesTheCopyOption()
    {
        WithFile(path =>
        {
            var saved = new RecordingSettings(true, true, true);
            saved.Save(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => (saved with { CopyAfterRecording = false }).Save(path));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(saved, RecordingSettings.Load(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        });
    }

    private static void WithFile(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-recording-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(Path.Combine(directory, "recording.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
