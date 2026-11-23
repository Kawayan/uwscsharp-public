using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class LogViewForwardingTests
{
    [Fact]
    public async Task ViewAndHistoryLimitPassThroughLoggerWithoutCreatingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-view-" + Guid.NewGuid() + ".log");
        var inner = new ViewHost();
        var logger = new ScriptLogHost(inner, path);
        await new Interpreter().RunAsync("OPTION LOGLINES=3\nLOGPRINT(FALSE,10,20)", logger);
        Assert.True(logger.SupportsLogView);
        Assert.Equal([3], inner.Limits);
        Assert.Equal([new ScriptLogView(false, 10, 20)], inner.Views);
        await logger.FlushAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PathChangeDoesNotChangeViewOrHistoryLimit()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-view-" + Guid.NewGuid() + ".log");
        string selected = Path.ChangeExtension(path, ".selected.log");
        var inner = new ViewHost();
        var logger = new ScriptLogHost(inner, path);
        await new Interpreter().RunAsync(
            $"OPTION LOGPATH=\"{selected}\"", logger);
        Assert.Empty(inner.Limits);
        Assert.Empty(inner.Views);
        await logger.FlushAsync();
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(selected));
    }

    [Fact]
    public async Task FileModeDoesNotHideViewOrChangeHistoryLimit()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-view-" + Guid.NewGuid() + ".log");
        var inner = new ViewHost();
        var logger = new ScriptLogHost(inner, path);
        await new Interpreter().RunAsync(
            "LOGPRINT(TRUE)\nOPTION LOGFILE=5", logger);
        Assert.Empty(inner.Limits);
        Assert.Equal([new ScriptLogView(true)], inner.Views);
        await logger.FlushAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LoggerDoesNotPretendConsoleCanShowLogWindow()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-view-" + Guid.NewGuid() + ".log");
        var logger = new ScriptLogHost(new CallbackScriptHost(_ => { }), path);
        Assert.False(logger.SupportsLogView);
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("LOGPRINT(TRUE)", logger));
        Assert.False(File.Exists(path));
    }

    private sealed class ViewHost : IScriptLogViewHost
    {
        internal List<int> Limits { get; } = [];

        internal List<ScriptLogView> Views { get; } = [];

        public ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken token)
        {
            Views.Add(view);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetLogHistoryLimitAsync(int maximumLines, CancellationToken token)
        {
            Limits.Add(maximumLines);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token) =>
            ValueTask.CompletedTask;

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
