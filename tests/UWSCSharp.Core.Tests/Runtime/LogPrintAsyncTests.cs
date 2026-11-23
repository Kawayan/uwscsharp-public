using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class LogPrintAsyncTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitsForViewAndKeepsAsyncFailureIdentity(bool fail)
    {
        var host = new PendingViewHost();
        Task run = new Interpreter().RunAsync(
            "TRY\nLOGPRINT(TRUE)\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\"", host);
        ScriptLogView request = await host.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.True(request.Visible);
        Assert.False(run.IsCompleted);
        Assert.Empty(host.Applied);
        Assert.Empty(host.Output);
        if (fail)
        {
            var failure = new IOException("view failed asynchronously");
            host.Release.SetException(failure);
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, error);
            Assert.Empty(host.Applied);
            Assert.Empty(host.Output);
        }
        else
        {
            host.Release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([new ScriptLogView(true)], host.Applied);
            Assert.Equal(["after"], host.Output);
        }
    }

    [Fact]
    public async Task CancellationWhileQueuedPreventsApplicationAndNextStatement()
    {
        var host = new PendingViewHost();
        using var cancellation = new CancellationTokenSource();
        Task run = new Interpreter().RunAsync(
            "LOGPRINT(TRUE)\nPRINT \"after\"", host, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        host.Release.SetResult();
        Assert.Empty(host.Applied);
        Assert.Empty(host.Output);
    }

    private sealed class PendingViewHost : IScriptLogViewHost
    {
        internal TaskCompletionSource<ScriptLogView> Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<ScriptLogView> Applied { get; } = [];

        internal List<string> Output { get; } = [];

        public async ValueTask SetLogViewAsync(ScriptLogView view,
            CancellationToken token)
        {
            Entered.SetResult(view);
            await Release.Task.WaitAsync(token);
            Applied.Add(view);
        }

        public ValueTask SetLogHistoryLimitAsync(int maximumLines,
            CancellationToken token) => ValueTask.CompletedTask;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
