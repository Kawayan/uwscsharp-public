using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

/// <summary>
/// 一時ファイルだけを操作し、画面・入力・クリップボードは使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LogByteLimitTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-log-bytes-" + Guid.NewGuid());

    public LogByteLimitTests() => Directory.CreateDirectory(directory);

    private string LogPath => Path.Combine(directory, "default.log");

    public void Dispose() => Directory.Delete(directory, true);

    private ScriptLogHost Host(int bytes, Action<string>? output = null, TimeProvider? clock = null) =>
        new(new CallbackScriptHost(output ?? (_ => { })), LogPath,
            new ScriptLogByteLimit(bytes), mode: ScriptLogMode.TextOnly, timeProvider: clock);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void InvalidByteLimitIsRejected(int maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptLogByteLimit(maximum));

    [Theory]
    [InlineData(2, "")]
    [InlineData(3, "b")]
    [InlineData(6, "あb")]
    [InlineData(10, "😀あb")]
    [InlineData(11, "a😀あb")]
    public async Task ByteLimitIncludesCrLfAndNeverSplitsUtf8(int maximum, string retained)
    {
        var displayed = new List<string>();
        ScriptLogHost host = Host(maximum, displayed.Add);
        await host.WriteOutputAsync("a😀あb", default);
        await host.FlushAsync();
        byte[] bytes = await File.ReadAllBytesAsync(LogPath);
        Assert.True(bytes.Length <= maximum);
        Assert.Equal(retained + "\r\n", new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal(["a😀あb"], displayed);
        Assert.Equal(11 - bytes.Length, host.OmittedDefaultBytes);
    }

    [Fact]
    public async Task FourMiBLimitRetainsNewestPendingOutput()
    {
        const int maximum = 4 * 1024 * 1024;
        ScriptLogHost host = Host(maximum);
        for (int index = 0; index < 100; index++)
        {
            await host.WriteOutputAsync(new string('x', 63_980) + index.ToString("D4"), default);
        }
        await host.FlushAsync();
        Assert.InRange(new FileInfo(LogPath).Length, 1, maximum);
        Assert.EndsWith("0099\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.True(host.OmittedDefaultBytes > 0);
    }

    [Fact]
    public async Task ExistingTailAndCachedMultilineEntriesRemainBounded()
    {
        await File.WriteAllTextAsync(LogPath, "old-line\r\n");
        ScriptLogHost host = Host(12);
        await host.WriteOutputAsync("a\r\nb", default);
        await host.FlushAsync();
        Assert.Equal("a\r\nb\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.Equal(10, host.OmittedDefaultBytes);
        await host.WriteOutputAsync("z", default);
        await host.FlushAsync();
        Assert.Equal("a\r\nb\r\nz\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.Equal(10, host.OmittedDefaultBytes);
    }

    [Fact]
    public async Task FailedSaveKeepsBoundedPendingDataAndDoesNotDoubleCount()
    {
        Directory.CreateDirectory(LogPath);
        ScriptLogHost host = Host(6);
        await host.WriteOutputAsync("old", default);
        await host.WriteOutputAsync("new", default);
        long omitted = host.OmittedDefaultBytes;
        Exception? error = await Record.ExceptionAsync(() => host.FlushAsync());
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(omitted, host.OmittedDefaultBytes);
        Directory.Delete(LogPath);
        await host.FlushAsync();
        Assert.Equal("new\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.Equal(omitted, host.OmittedDefaultBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPathIsNotLimitedAndEmptyPathRestoresDefault(bool samePath)
    {
        ScriptLogHost host = Host(6);
        string selected = samePath ? LogPath : Path.Combine(directory, "explicit.log");
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default);
        await host.ConfigureLogAsync(new ScriptLogSettings(MaximumLines: 2), default);
        await host.WriteOutputAsync("explicit-long-line", default);
        await host.FlushAsync();
        Assert.Equal("explicit-long-line\r\n", await File.ReadAllTextAsync(selected));
        Assert.Equal(0, host.OmittedDefaultBytes);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: ""), default);
        await host.WriteOutputAsync("latest", default);
        await host.FlushAsync();
        Assert.Equal("test\r\n", await File.ReadAllTextAsync(LogPath));
        if (!samePath)
        {
            Assert.Equal("explicit-long-line\r\n", await File.ReadAllTextAsync(selected));
        }
    }

    [Fact]
    public async Task ExistingConstructorDoesNotApplyNewBytePolicy()
    {
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }), LogPath,
            mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("unlimited-by-byte-policy", default);
        await host.FlushAsync();
        Assert.Equal("unlimited-by-byte-policy\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.Equal(0, host.OmittedDefaultBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task FinalSaveFailureIsReportedWithoutChangingExitCode(int exitCode)
    {
        Directory.CreateDirectory(LogPath);
        ScriptLogHost host = Host(64);
        var errors = new List<Exception>();
        int code = await host.RunWithPeriodicFlushAsync(async output =>
        {
            await output.WriteOutputAsync("one", default);
            return exitCode;
        }, errors.Add);
        Assert.Equal(exitCode, code);
        Assert.Single(errors);
    }

    [Fact]
    public async Task OriginalExecutionFailureSurvivesFinalSaveFailure()
    {
        Directory.CreateDirectory(LogPath);
        ScriptLogHost host = Host(64);
        var errors = new List<Exception>();
        var primary = new InvalidOperationException("script failure");
        Exception result = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RunWithPeriodicFlushAsync(async output =>
            {
                await output.WriteOutputAsync("one", default);
                throw primary;
            }, errors.Add));
        Assert.Same(primary, result);
        Assert.Single(errors);
    }

    [Fact]
    public async Task PeriodicSaveRunsBeforeScriptCompletionAndFinalSaveJoinsTimer()
    {
        var clock = new TickClock();
        ScriptLogHost host = Host(64, clock: clock);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> execution = host.RunWithPeriodicFlushAsync(async output =>
        {
            await output.WriteOutputAsync("one", default);
            ready.SetResult();
            await complete.Task;
            await output.WriteOutputAsync("two", default);
            return 7;
        }, error => Assert.Fail(error.Message));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Tick();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? saved = null;
            while (saved is null)
            {
                try
                {
                    await using var stream = new FileStream(LogPath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        4096, FileOptions.Asynchronous);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    saved = await reader.ReadToEndAsync(timeout.Token);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                {
                }
                if (saved is null)
                {
                    await Task.Delay(1, timeout.Token);
                }
            }
            Assert.Equal("one\r\n", saved);
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            complete.TrySetResult();
            await execution;
        }
        Assert.Equal(7, await execution);
        Assert.Equal("one\r\ntwo\r\n", await File.ReadAllTextAsync(LogPath));
        Assert.True(clock.Disposed);
    }

    private sealed class TickClock : TimeProvider, ITimer
    {
        private TimerCallback? callback;
        private object? state;

        internal bool Disposed
        {
            get; private set;
        }

        internal void Tick() => callback!(state);

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            return this;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
