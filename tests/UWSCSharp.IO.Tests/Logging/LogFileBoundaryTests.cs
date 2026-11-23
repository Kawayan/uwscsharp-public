using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogFileBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-file-" + Guid.NewGuid());

    public LogFileBoundaryTests()
    {
        Directory.CreateDirectory(directory);
    }

    private string LogPath => Path.Combine(directory, "selected.log");

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(0, "old\r\n26/10/22 03:04  first\r\n26/10/22 03:06  second\r\n")]
    [InlineData(1, "old\r\n")]
    [InlineData(2, "old\r\nfirst\r\nsecond\r\n")]
    [InlineData(3, "old\r\n26/10/22 03:04:05  first\r\n26/10/22 03:06:07  second\r\n")]
    [InlineData(4, "26/10/22 03:04  first\r\n26/10/22 03:06  second\r\n")]
    [InlineData(5, "old\r\n")]
    [InlineData(6, "first\r\nsecond\r\n")]
    [InlineData(7, "26/10/22 03:04:05  first\r\n26/10/22 03:06:07  second\r\n")]
    public async Task AllModesKeepPrintTimeAndClearOnlyOnFirstSave(
        int mode, string expected)
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        var clock = new FixedClock();
        List<string> output = [];
        var host = new ScriptLogHost(new CallbackScriptHost(output.Add),
            LogPath, timeProvider: clock);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: mode), default);
        await host.FlushAsync();
        Assert.Equal("old\r\n", await File.ReadAllTextAsync(LogPath));
        await host.WriteOutputAsync("first", default);
        clock.Now = clock.Now.AddMinutes(2).AddSeconds(2);
        await host.FlushAsync();
        await host.WriteOutputAsync("second", default);
        await host.FlushAsync();
        Assert.Equal(["first", "second"], output);
        Assert.Equal(expected, await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task ClearFlagSurvivesFailedReplacementUntilSuccessfulRetry()
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            LogPath, mode: ScriptLogMode.TextOnly);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 6), default);
        await host.WriteOutputAsync("first", default);
        using (var locked = new FileStream(LogPath, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            Exception? error = await Record.ExceptionAsync(() => host.FlushAsync());
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("old\r\n", await File.ReadAllTextAsync(LogPath));
            Assert.Empty(Directory.GetFiles(directory, ".uwsc-log-*.tmp"));
        }

        await host.WriteOutputAsync("second", default);
        await host.FlushAsync();
        await host.WriteOutputAsync("third", default);
        await host.FlushAsync();
        Assert.Equal("first\r\nsecond\r\nthird\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task OldSaveFailureDoesNotApplyNewClearMode()
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            LogPath, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("first", default);
        using (var locked = new FileStream(LogPath, FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            Exception? error = await Record.ExceptionAsync(async () =>
                await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 6), default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        await host.WriteOutputAsync("second", default);
        await host.FlushAsync();
        Assert.Equal("old\r\nfirst\r\nsecond\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task LaterModeReplacesUnconsumedClearFlag(int replacement)
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            LogPath, mode: ScriptLogMode.TextOnly);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 6), default);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: replacement), default);
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        string expected = replacement == 2 ? "old\r\nafter\r\n" : "old\r\n";
        Assert.Equal(expected, await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task RepeatingClearModeRearmsClearForNextSave()
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 6), default);
        await host.WriteOutputAsync("first", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 6), default);
        Assert.Equal("first\r\n", await File.ReadAllTextAsync(LogPath));
        await host.WriteOutputAsync("second", default);
        await host.FlushAsync();
        Assert.Equal("second\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task InvalidModeDoesNotFlushPendingOutput()
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            LogPath, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("first", default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await host.ConfigureLogAsync(new ScriptLogSettings(FileMode: 8), default));
        Assert.False(File.Exists(LogPath));
        await host.FlushAsync();
        Assert.Equal("first\r\n", await File.ReadAllTextAsync(LogPath));
    }

    private sealed class FixedClock : TimeProvider
    {
        internal DateTimeOffset Now
        {
            get; set;
        } = new(2026, 10, 22, 3, 4, 5, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
