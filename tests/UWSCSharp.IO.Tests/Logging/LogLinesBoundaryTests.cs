using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogLinesBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-lines-" + Guid.NewGuid());

    public LogLinesBoundaryTests()
    {
        Directory.CreateDirectory(directory);
    }

    private string LogPath => Path.Combine(directory, "selected.log");

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    private ScriptLogHost CreateHost(int maximum = 3) => new(
        new CallbackScriptHost(_ => { }), LogPath, maximum,
        ScriptLogMode.TextOnly);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100000)]
    public async Task MaximumRetainsNewestEntriesIncludingUpperBoundary(int maximum)
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        ScriptLogHost host = CreateHost(maximum);
        for (int index = 0; index <= maximum; index++)
        {
            await host.WriteOutputAsync(index.ToString(
                System.Globalization.CultureInfo.InvariantCulture), default);
        }

        await host.FlushAsync();
        string[] lines = await File.ReadAllLinesAsync(LogPath);
        if (maximum == 0)
        {
            Assert.Equal(["old"], lines);
        }
        else
        {
            Assert.Equal(maximum, lines.Length);
            Assert.Equal("1", lines[0]);
            Assert.Equal(maximum.ToString(
                System.Globalization.CultureInfo.InvariantCulture), lines[^1]);
        }
    }

    [Fact]
    public async Task ShrinkSavesOldPendingLimitBeforeApplyingNewLimit()
    {
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("one", default);
        await host.WriteOutputAsync("two", default);
        await host.WriteOutputAsync("three", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: 1), default);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", await File.ReadAllTextAsync(LogPath));
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal("after\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task IncreasingLimitDoesNotRestoreDiscardedPendingEntries()
    {
        ScriptLogHost host = CreateHost(1);
        await host.WriteOutputAsync("one", default);
        await host.WriteOutputAsync("two", default);
        await host.WriteOutputAsync("three", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: 3), default);
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal("three\r\nafter\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task ZeroIntervalStillDisplaysWithoutRestoringIgnoredOutput()
    {
        await File.WriteAllTextAsync(LogPath, "old1\r\nold2\r\n");
        List<string> output = [];
        var host = new ScriptLogHost(new CallbackScriptHost(output.Add),
            LogPath, maximumLines: 0, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("ignored", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: 2), default);
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal(["ignored", "after"], output);
        Assert.Equal("old2\r\nafter\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task FailedOldSaveKeepsPreviousLimitAndPendingEntries()
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        ScriptLogHost host = CreateHost();
        await host.WriteOutputAsync("one", default);
        await host.WriteOutputAsync("two", default);
        using (var locked = new FileStream(LogPath, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            Exception? error = await Record.ExceptionAsync(async () =>
                await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: 1), default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        await host.WriteOutputAsync("three", default);
        await host.FlushAsync();
        Assert.Equal("one\r\ntwo\r\nthree\r\n", await File.ReadAllTextAsync(LogPath));
    }
}
