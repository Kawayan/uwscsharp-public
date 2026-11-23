using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class MultilineLogRetentionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-multiline-" + Guid.NewGuid());

    public MultilineLogRetentionTests()
    {
        Directory.CreateDirectory(directory);
    }

    private string LogPath => Path.Combine(directory, "UWSC.LOG");

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    private ScriptLogHost CreateHost(int maximum = 2) => new(
        new CallbackScriptHost(_ => { }), LogPath, maximum,
        ScriptLogMode.TextOnly);

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public async Task SavingDoesNotSplitPreviouslySavedPrintEntry(string separator)
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a" + separator + "b", default);
        await host.FlushAsync();
        await host.WriteOutputAsync("tail", default);
        await host.FlushAsync();
        Assert.Equal("a" + separator + "b\r\ntail\r\n",
            await File.ReadAllTextAsync(LogPath));
        await host.WriteOutputAsync("last", default);
        await host.FlushAsync();
        Assert.Equal("tail\r\nlast\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task ExternalReplacementInvalidatesSavedEntryBoundaries()
    {
        ScriptLogHost host = CreateHost(3);
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        DateTime timestamp = File.GetLastWriteTimeUtc(LogPath);
        await File.WriteAllTextAsync(LogPath, "x\r\ny\r\n");
        File.SetLastWriteTimeUtc(LogPath, timestamp);
        await host.WriteOutputAsync("tail", default);
        await host.FlushAsync();
        Assert.Equal("x\r\ny\r\ntail\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task NewHostReadsExistingFileAsPhysicalLines()
    {
        ScriptLogHost first = CreateHost();
        await first.WriteOutputAsync("a\r\nb", default);
        await first.FlushAsync();
        ScriptLogHost second = CreateHost();
        await second.WriteOutputAsync("tail", default);
        await second.FlushAsync();
        Assert.Equal("b\r\ntail\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task OtherHostOutputSurvivesAfterMultilineSave()
    {
        ScriptLogHost first = CreateHost(4);
        await first.WriteOutputAsync("a\r\nb", default);
        await first.FlushAsync();
        ScriptLogHost second = CreateHost(4);
        await second.WriteOutputAsync("other", default);
        await second.FlushAsync();
        await first.WriteOutputAsync("tail", default);
        await first.FlushAsync();
        Assert.Equal("a\r\nb\r\nother\r\ntail\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task SameBytesAtDifferentPathDoNotReuseEntryBoundaries()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        string other = Path.Combine(directory, "other.log");
        File.Copy(LogPath, other);
        await host.ConfigureLogAsync(new ScriptLogSettings { Path = other }, default);
        await host.WriteOutputAsync("tail", default);
        await host.FlushAsync();
        Assert.Equal("b\r\ntail\r\n", await File.ReadAllTextAsync(other));
        Assert.Equal("a\r\nb\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task ClearOptionDiscardsPreviouslySavedEntries()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        await host.ConfigureLogAsync(new ScriptLogSettings { FileMode = 6 }, default);
        await host.WriteOutputAsync("tail", default);
        await host.FlushAsync();
        Assert.Equal("tail\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task DeletedFileDoesNotRestoreOldSavedEntries()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        File.Delete(LogPath);
        await host.WriteOutputAsync("tail", default);
        await host.FlushAsync();
        Assert.Equal("tail\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task CancellationKeepsSavedEntriesForRetry()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        await host.WriteOutputAsync("tail", default);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.FlushAsync(stop.Token));
        await host.FlushAsync();
        Assert.Equal("a\r\nb\r\ntail\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task FailedReadKeepsSavedEntriesForRetry()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        await host.WriteOutputAsync("tail", default);
        using (FileStream locked = File.Open(LogPath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => host.FlushAsync());
        }

        await host.FlushAsync();
        Assert.Equal("a\r\nb\r\ntail\r\n", await File.ReadAllTextAsync(LogPath));
    }
}
