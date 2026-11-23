using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogPathBoundaryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-log-path-" + Guid.NewGuid());

    public LogPathBoundaryTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("plain.log", "plain.log")]
    [InlineData("ログ 日本語.log", "ログ 日本語.log")]
    [InlineData("no-extension", "no-extension")]
    [InlineData("logs/../selected.log", "selected.log")]
    [InlineData("logs", "logs/UWSC.LOG")]
    [InlineData("logs/", "logs/UWSC.LOG")]
    [InlineData(".", "UWSC.LOG")]
    [InlineData("", "UWSC.LOG")]
    public async Task ResolvesPathAgainstScriptRatherThanProcessDirectory(
        string selected, string relative)
    {
        string script = Path.Combine(directory, "main.uws");
        await ScriptLogHost.RunScriptAsync(
            new CallbackScriptHost(_ => { }), script,
            host => new Interpreter().RunWithExitCodeAsync(
                $"OPTION LOGFILE=2, LOGPATH=\"{selected}\"\nPRINT \"saved\"",
                host, default, ScriptRunOptions.Empty));
        Assert.Equal("saved\r\n", await File.ReadAllTextAsync(
            Path.Combine(directory, relative)));
        Assert.Single(Directory.GetFiles(directory, "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LongNestedPathHasNoLegacy260CharacterLimit()
    {
        string selected = directory;
        for (int index = 0; index < 8; index++)
        {
            selected = Path.Combine(selected, new string('a', 40));
        }

        Directory.CreateDirectory(selected);
        selected = Path.Combine(selected, "selected.log");
        Assert.True(selected.Length > 260);
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            Path.Combine(directory, "UWSC.LOG"), mode: ScriptLogMode.TextOnly);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default);
        await host.WriteOutputAsync("saved", default);
        await host.FlushAsync();
        Assert.Equal("saved\r\n", await File.ReadAllTextAsync(selected));
    }

    [Fact]
    public async Task MissingDirectoryIsNotCreatedAndSaveFailureIsReported()
    {
        string selected = Path.Combine(directory, "missing", "selected.log");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            Path.Combine(directory, "UWSC.LOG"), mode: ScriptLogMode.TextOnly);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default);
        await host.WriteOutputAsync("retry", default);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => host.FlushAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(selected)));
        Directory.CreateDirectory(Path.GetDirectoryName(selected)!);
        await host.FlushAsync();
        Assert.Equal("retry\r\n", await File.ReadAllTextAsync(selected));
    }

    [Fact]
    public async Task AbsolutePathAndEmptyResetKeepSeparateOutput()
    {
        string initial = Path.Combine(directory, "UWSC.LOG");
        string selected = Path.Combine(directory, "logs", "selected.log");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            initial, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("before", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default);
        await host.WriteOutputAsync("selected", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: ""), default);
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal("before\r\nafter\r\n", await File.ReadAllTextAsync(initial));
        Assert.Equal("selected\r\n", await File.ReadAllTextAsync(selected));
    }

    [Theory]
    [InlineData("bad\0path")]
    [InlineData("bad\tpath")]
    [InlineData("bad\rpath")]
    [InlineData("bad\npath")]
    public async Task InvalidPathKeepsPendingOutputWithoutFlushing(string selected)
    {
        string initial = Path.Combine(directory, "UWSC.LOG");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            initial, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("before", default);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default));
        Assert.False(File.Exists(initial));
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal("before\r\nafter\r\n", await File.ReadAllTextAsync(initial));
    }

    [Fact]
    public async Task OldSaveFailureKeepsOldPathAndPendingOutputForRetry()
    {
        string blocked = Path.Combine(directory, "blocked.log");
        string selected = Path.Combine(directory, "selected.log");
        Directory.CreateDirectory(blocked);
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            blocked, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("before", default);
        Exception? error = await Record.ExceptionAsync(async () =>
            await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default));
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.False(File.Exists(selected));
        Directory.Delete(blocked);
        await host.WriteOutputAsync("after failure", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: selected), default);
        await host.WriteOutputAsync("selected", default);
        await host.FlushAsync();
        Assert.Equal("before\r\nafter failure\r\n", await File.ReadAllTextAsync(blocked));
        Assert.Equal("selected\r\n", await File.ReadAllTextAsync(selected));
        Assert.Empty(Directory.GetFiles(directory, ".uwsc-log-*.tmp"));
    }

    [Fact]
    public async Task CancelledChangeKeepsOldPathAndPendingOutput()
    {
        string initial = Path.Combine(directory, "UWSC.LOG");
        var host = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            initial, mode: ScriptLogMode.TextOnly);
        await host.WriteOutputAsync("before", default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await host.ConfigureLogAsync(
                new ScriptLogSettings(Path: "selected.log"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(File.Exists(initial));
        await host.FlushAsync();
        Assert.Equal("before\r\n", await File.ReadAllTextAsync(initial));
        Assert.False(File.Exists(Path.Combine(directory, "selected.log")));
    }
}
