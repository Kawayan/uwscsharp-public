using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class SleepAuditTests
{
    private sealed class Host : IScriptHost
    {
        internal List<TimeSpan> Waits { get; } = [];

        internal List<string> Output { get; } = [];

        internal Action? OnDelay
        {
            get; init;
        }

        internal bool UseDefaultDelay
        {
            get; init;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Waits.Add(duration);
            OnDelay?.Invoke();
            return UseDefaultDelay
                ? new CallbackScriptHost(Output.Add).DelayAsync(duration, token)
                : Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("\"0.001\"", 0.001)]
    [InlineData("\"1e-3\"", 0.001)]
    [InlineData("\"0\"", 0)]
    [InlineData("EMPTY", 0)]
    [InlineData("\"2147483.647\"", 2147483.647)]
    public async Task NumericSecondsReachTheHostWithoutRealWaiting(string expression, double seconds)
    {
        var host = new Host();
        await new Interpreter().RunAsync("SLEEP(" + expression + ")\nPRINT 1", host);
        Assert.Equal([TimeSpan.FromSeconds(seconds)], host.Waits);
        Assert.Equal(["1"], host.Output);
    }

    [Theory]
    [InlineData("\"bad\"")]
    [InlineData("\"-0.001\"")]
    [InlineData("\"2147483.648\"")]
    [InlineData("\"1e999\"")]
    [InlineData("NULL")]
    public async Task InvalidSecondsFailBeforeHostWaiting(string expression)
    {
        var host = new Host();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nSLEEP(" + expression + ")", host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Waits);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("2147483.647")]
    public async Task DefaultHostWaitAcceptsCancellationAfterStartingStringSleep(string seconds)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new Host
        {
            UseDefaultDelay = true,
            OnDelay = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(20))
        };
        Task run = new Interpreter().RunAsync($"SLEEP(\"{seconds}\")\nPRINT 1", host, cancellation.Token);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal([TimeSpan.FromSeconds(double.Parse(seconds,
            System.Globalization.CultureInfo.InvariantCulture))], host.Waits);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task StringSecondsStillHonorCancellationAfterHostReturns()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new Host { OnDelay = cancellation.Cancel };
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("SLEEP(\"1\")\nPRINT 1", host, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal([TimeSpan.FromSeconds(1)], host.Waits);
        Assert.Empty(host.Output);
    }
}
