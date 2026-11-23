using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptLogHostTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-" + Guid.NewGuid());

    public ScriptLogHostTests()
    {
        Directory.CreateDirectory(directory);
    }

    private string LogPath => Path.Combine(directory, "UWSC.LOG");

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task InterpreterOutputIsSavedAsUtf8WithoutBomAndWithCrLf()
    {
        List<string> output = [];
        int code = await ScriptLogHost.RunAsync(new CallbackScriptHost(output.Add),
            LogPath, host => new Interpreter().RunWithExitCodeAsync(
                "PRINT\nPRINT \"日本語😀\"\nEXITEXIT 7", host,
                default, ScriptRunOptions.Empty),
            mode: ScriptLogMode.TextOnly);
        Assert.Equal(7, code);
        Assert.Equal(["", "日本語😀"], output);
        Assert.Equal(Encoding.UTF8.GetBytes("\r\n日本語😀\r\n"),
            await File.ReadAllBytesAsync(LogPath));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(ScriptLogMode.Minutes, "26/10/22 03:04  value\r\n")]
    [InlineData(ScriptLogMode.Seconds, "26/10/22 03:04:05  value\r\n")]
    [InlineData(ScriptLogMode.TextOnly, "value\r\n")]
    public async Task ModesUseInjectedClockAndInvariantFormat(
        ScriptLogMode mode, string expected)
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: mode, timeProvider: new FixedClock());
        await host.WriteOutputAsync("value", default);
        Assert.False(File.Exists(LogPath));
        await host.FlushAsync();
        Assert.Equal(expected, await File.ReadAllTextAsync(LogPath));
    }

    [Theory]
    [InlineData(ScriptLogMode.Disabled, 10)]
    [InlineData(ScriptLogMode.TextOnly, 0)]
    public async Task DisabledLoggingStillDisplaysAndDoesNotTouchExistingFile(
        ScriptLogMode mode, int maximum)
    {
        await File.WriteAllTextAsync(LogPath, "untouched");
        List<string> output = [];
        var host = new ScriptLogHost(new CallbackScriptHost(output.Add), LogPath,
            maximum, mode, clearExisting: true);
        await host.WriteOutputAsync("visible", default);
        await host.FlushAsync();
        Assert.Equal(["visible"], output);
        Assert.Equal("untouched", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task RetentionMergesExistingTailAndDoesNotDuplicateOnRepeatedFlush()
    {
        await File.WriteAllTextAsync(LogPath, "old1\nold2\nold3\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            maximumLines: 3, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("new1", default);
        await host.WriteOutputAsync("new2", default);
        await host.FlushAsync();
        await host.FlushAsync();
        Assert.Equal("old3\r\nnew1\r\nnew2\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task ClearExistingIsAppliedOnlyOnFirstSuccessfulSave()
    {
        await File.WriteAllTextAsync(LogPath, "old\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly, clearExisting: true);
        await host.WriteOutputAsync("new1", default);
        await host.FlushAsync();
        await host.WriteOutputAsync("new2", default);
        await host.FlushAsync();
        Assert.Equal("new1\r\nnew2\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task PendingOutputsKeepNewestEntriesBeforeFirstSave()
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            maximumLines: 2, mode: ScriptLogMode.TextOnly);
        foreach (string value in new[] { "a", "b", "c", "d" })
        {
            await host.WriteOutputAsync(value, default);
        }

        await host.FlushAsync();
        Assert.Equal("c\r\nd\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task LongTextIsTruncatedWithoutChangingDisplayOrSplittingPair()
    {
        string prefix = new('a', 63_999);
        string text = prefix + "😀tail";
        List<string> output = [];
        var host = new ScriptLogHost(new CallbackScriptHost(output.Add), LogPath,
            mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync(text, default);
        await host.FlushAsync();
        Assert.Equal([text], output);
        Assert.Equal(prefix + "...\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task CancellationAfterDisplayStillSavesAcceptedOutput()
    {
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptLogHost.RunAsync(new CallbackScriptHost(_ => cancellation.Cancel()),
                LogPath, host => new Interpreter().RunWithExitCodeAsync(
                    "PRINT \"accepted\"\nPRINT \"skipped\"", host,
                    cancellation.Token, ScriptRunOptions.Empty),
                mode: ScriptLogMode.TextOnly));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal("accepted\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task PrimaryFailureAndExistingCleanupErrorsSurviveSaveFailure()
    {
        Directory.CreateDirectory(LogPath);
        var primary = new InvalidOperationException("primary");
        var previous = new IOException("previous cleanup");
        primary.Data["UWSCSharp.CleanupErrors"] = new AggregateException(previous);
        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScriptLogHost.RunAsync(new CallbackScriptHost(_ => { }), LogPath,
                async host =>
                {
                    await host.WriteOutputAsync("pending", default);
                    throw primary;
                }, mode: ScriptLogMode.TextOnly));
        Assert.Same(primary, observed);
        AggregateException errors = Assert.IsType<AggregateException>(
            observed.Data["UWSCSharp.CleanupErrors"]);
        Assert.Same(previous, errors.InnerExceptions[0]);
        Assert.Equal(2, errors.InnerExceptions.Count);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task SaveFailureRetainsPendingDataAndAllowsRetry()
    {
        Directory.CreateDirectory(LogPath);
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly, clearExisting: true);
        await host.WriteOutputAsync("retry", default);
        Exception? failure = await Record.ExceptionAsync(() => host.FlushAsync());
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(directory));
        Directory.Delete(LogPath);
        await host.FlushAsync();
        Assert.Equal("retry\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task InvalidTextDoesNotReplaceExistingFile()
    {
        await File.WriteAllTextAsync(LogPath, "original");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("\ud800", default);
        await Assert.ThrowsAnyAsync<EncoderFallbackException>(() => host.FlushAsync());
        Assert.Equal("original", await File.ReadAllTextAsync(LogPath));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task ConcurrentHostsMergeWithoutLosingCompletedOutputs()
    {
        Task[] writes = [.. Enumerable.Range(0, 12).Select(async index =>
        {
            var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
                mode: ScriptLogMode.TextOnly);
            await host.WriteOutputAsync(index.ToString(), default);
            await host.FlushAsync();
        })];
        await Task.WhenAll(writes);
        string[] lines = await File.ReadAllLinesAsync(LogPath);
        Assert.Equal(12, lines.Length);
        Assert.Equal(Enumerable.Range(0, 12).Order(),
            lines.Select(int.Parse).Order());
    }

    [Fact]
    public async Task ScriptFailureFlushesEarlierOutputAndPreservesLine()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            ScriptLogHost.RunAsync(new CallbackScriptHost(_ => { }), LogPath,
                host => new Interpreter().RunWithExitCodeAsync(
                    "PRINT \"before\"\nPRINT 1/0", host,
                    default, ScriptRunOptions.Empty), mode: ScriptLogMode.TextOnly));
        Assert.Equal(2, error.Line);
        Assert.Equal("before\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task DisplayFailureIsNotRecordedAsSuccessfulOutput()
    {
        var failure = new IOException("display failed");
        IOException observed = await Assert.ThrowsAsync<IOException>(() =>
            ScriptLogHost.RunAsync(new CallbackScriptHost(_ => throw failure),
                LogPath, host => new Interpreter().RunWithExitCodeAsync(
                    "PRINT \"not accepted\"", host, default, ScriptRunOptions.Empty),
                mode: ScriptLogMode.TextOnly));
        Assert.Same(failure, observed);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task CancelledFlushDoesNotChangeFileAndCanBeRetried()
    {
        await File.WriteAllTextAsync(LogPath, "old\r\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("new", default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.FlushAsync(cancellation.Token));
        Assert.Equal("old\r\n", await File.ReadAllTextAsync(LogPath));
        await host.FlushAsync();
        Assert.Equal("old\r\nnew\r\n", await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task CancellationAndSaveFailureKeepOriginalToken()
    {
        Directory.CreateDirectory(LogPath);
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptLogHost.RunAsync(new CallbackScriptHost(_ => cancellation.Cancel()),
                LogPath, host => new Interpreter().RunWithExitCodeAsync(
                    "PRINT 1", host, cancellation.Token, ScriptRunOptions.Empty),
                mode: ScriptLogMode.TextOnly));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.CleanupErrors"]);
        Assert.Single(cleanup.InnerExceptions);
    }

    [Fact]
    public async Task ExistingLongLineAndMixedEndingsAreReadWithBoundedEntries()
    {
        string prefix = new('x', 64_031);
        await File.WriteAllTextAsync(LogPath,
            "discarded\r\n" + prefix + "😀" + new string('y', 200_000)
            + "\rsecond\n\r\n");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            maximumLines: 4, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("new", default);
        await host.FlushAsync();
        Assert.Equal(prefix + "...\r\nsecond\r\n\r\nnew\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Fact]
    public async Task InvalidExistingUtf8IsNotReplacedAndCanBeCorrectedForRetry()
    {
        byte[] original = [0xff, 0xff, 0xff];
        await File.WriteAllBytesAsync(LogPath, original);
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("pending", default);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => host.FlushAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(LogPath));
        await File.WriteAllTextAsync(LogPath, "corrected\r\n");
        await host.FlushAsync();
        Assert.Equal("corrected\r\npending\r\n",
            await File.ReadAllTextAsync(LogPath));
    }

    [Theory]
    [InlineData(-1, ScriptLogMode.TextOnly)]
    [InlineData(100_001, ScriptLogMode.TextOnly)]
    [InlineData(1, (ScriptLogMode)99)]
    public void InvalidOptionsFailWithoutCreatingFiles(int maximum, ScriptLogMode mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath, maximum, mode));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void RelativePathFailsWithoutDependingOnCurrentDirectory()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScriptLogHost(new CallbackScriptHost(_ => { }), "UWSC.LOG"));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() =>
            new(2026, 10, 22, 3, 4, 5, TimeSpan.Zero);
    }
}
