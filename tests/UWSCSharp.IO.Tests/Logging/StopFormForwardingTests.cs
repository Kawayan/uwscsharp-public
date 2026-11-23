using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopFormForwardingTests
{
    [Fact]
    public async Task LoggingKeepsThePlaybackCapabilityAndTransfersItsRequest()
    {
        var host = new Host();
        var logger = new ScriptLogHost(host, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
        Assert.True(logger.SupportsStopForm);
        await new Interpreter().RunAsync("STOPFORM(TRUE,80,90)\nSTOPFORM(FALSE)", logger);
        Assert.Equal(new ScriptStopForm[] { new(true, 80, 90), new(false) }, host.Requests);
        await logger.FlushAsync();
    }

    [Fact]
    public async Task LoggingDoesNotInventSupportForAnOutputOnlyHost()
    {
        var logger = new ScriptLogHost(new CallbackScriptHost(_ => { }),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
        Assert.False(logger.SupportsStopForm);
        Assert.False(logger.SupportsStopFormOptions);
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync("STOPFORM(TRUE)", logger));
        await logger.FlushAsync();
    }

    [Fact]
    public async Task LoggingForwardsTheOptionsSessionAndItsRestoration()
    {
        var host = new Host();
        var logger = new ScriptLogHost(host, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
        Assert.True(logger.SupportsStopFormOptions);
        await new Interpreter().RunAsync("OPTION TOPSTOPFORM,NOSTOPHOTKEY", logger);
        Assert.Equal([ScriptStopFormOption.TopMost, ScriptStopFormOption.DisableStopHotkey], host.Options);
        Assert.True(host.Restored);
    }

    private sealed class Host : IScriptStopFormOptionsHost, IScriptStopFormOptionsSession
    {
        internal readonly List<ScriptStopForm> Requests = [];
        internal readonly List<ScriptStopFormOption> Options = [];
        internal bool Restored;

        public ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IScriptStopFormOptionsSession>(this);

        public ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken cancellationToken)
        {
            Options.Add(option);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Restored = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken token)
        {
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token) => ValueTask.CompletedTask;

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);
    }
}
