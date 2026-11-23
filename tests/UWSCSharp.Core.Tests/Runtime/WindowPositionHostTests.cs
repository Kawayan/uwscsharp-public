using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowPositionHostTests
{
    [Fact]
    public async Task CoordinatesAreEvaluatedOnceInOrderBeforeOneHostRequest()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            OPTION POSITION=Axis(10.75),Axis(-20.75)
            PROCEDURE Unused()
            FEND
            FUNCTION Axis(value)
                PRINT value
                RESULT=value
            FEND
            """, host);
        Assert.Equal(["10.75", "-20.75", "move:10,-20"], host.Events);
    }

    [Fact]
    public async Task UnsupportedHostRejectsBeforeExpressionSideEffects()
    {
        var host = new Host { SupportsWindowPosition = false };
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync("""
            OPTION POSITION=Axis(),Axis()
            FUNCTION Axis()
                PRINT "side-effect"
                RESULT=1
            FEND
            """, host));
        Assert.Empty(host.Events);
    }

    [Theory]
    [InlineData("OPTION POSITION=1,2147483648")]
    [InlineData("OPTION POSITION=\"invalid\",2")]
    public async Task InvalidCoordinatesDoNotPartiallyMoveWindows(string source)
    {
        var host = new Host();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync(source, host));
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task PositionAwaitsHostCompletionAndObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host
        {
            Move = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        Task run = new Interpreter().RunAsync("OPTION POSITION=1,2\nPRINT \"after\"",
            host, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["move:1,2"], host.Events);
    }

    [Fact]
    public async Task WorkerUsesTheSameHostAndLaterExecutionUsesItsOwnHost()
    {
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Host { Move = _ => { moved.SetResult(); return Task.CompletedTask; } };
        using var cancellation = new CancellationTokenSource();
        var interpreter = new Interpreter();
        Task run = interpreter.RunAsync("""
            THREAD Move()
            SLEEP(30)
            PROCEDURE Move()
                OPTION POSITION=80,90
            FEND
            """, first, cancellation.Token);
        await moved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var second = new Host();
        await interpreter.RunAsync("OPTION POSITION=100,120", second);
        Assert.Equal(["move:80,90"], first.Events);
        Assert.Equal(["move:100,120"], second.Events);
    }

    private sealed class Host : IScriptWindowPositionHost
    {
        internal List<string> Events = [];
        internal Func<CancellationToken, Task>? Move;

        public bool SupportsWindowPosition { get; init; } = true;

        public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
            CancellationToken token)
        {
            Events.Add($"move:{request.X},{request.Y}");
            return new(Move?.Invoke(token) ?? Task.CompletedTask);
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Events.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);
    }
}
