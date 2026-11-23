using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class StopFormHostTests
{
    [Fact]
    public async Task VisibilityOmissionsAndStringReturnAreRoutedToTheHost()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            PRINT "["+STOPFORM(TRUE,10.9,-20.9)+"]"
            STOPFORM(0.5)
            STOPFORM(TRUE,,140)
            STOPFORM(FALSE,0,0)
            """, host);
        Assert.Equal(["[]"], host.Output);
        Assert.Equal<ScriptStopForm>(
        [
            new(true,10,-20), new(false), new(true,Y:140), new(false,0,0)
        ], host.Requests);
    }

    [Theory]
    [InlineData("STOPFORM()")]
    [InlineData("STOPFORM(,Tick())")]
    [InlineData("STOPFORM(TRUE,0,0,Tick())")]
    public async Task InvalidShapeDoesNotEvaluateArguments(string call)
    {
        var host = new Host();
        await new Interpreter().RunAsync("PUBLIC ticks=0\nTRY\n" + call + "\nEXCEPT\nPRINT ticks\nENDTRY\n" + """
            FUNCTION Tick()
                ticks=ticks+1
                RESULT=1
            FEND
            """, host);
        Assert.Equal(["0"], host.Output);
        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData("STOPFORM(\"bad\")")]
    [InlineData("STOPFORM(TRUE,2147483648)")]
    [InlineData("STOPFORM(FALSE,,\"bad\")")]
    public async Task InvalidValuesKeepTheCallLineAndDoNotReachTheHost(string call)
    {
        var host = new Host();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + call, host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task UnsupportedHostDoesNotEvaluateTheFlag()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC ticks=0
            TRY
                STOPFORM(Tick())
            EXCEPT
                PRINT ticks
            ENDTRY
            FUNCTION Tick()
                ticks=ticks+1
                RESULT=1
            FEND
            """, output.Add);
        Assert.Equal(["0"], output);
    }

    [Fact]
    public async Task CancellationDuringHostWorkDoesNotExecuteTheNextStatement()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host
        {
            Apply = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        Task run = new Interpreter().RunAsync("STOPFORM(TRUE)\nPRINT \"after\"", host, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task WorkersAndEvalUseTheSameHost()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            PUBLIC finished=FALSE
            THREAD Child()
            WHILE finished=FALSE
                SLEEP(0.01)
            WEND
            EVAL("STOPFORM(FALSE)")
            PROCEDURE Child()
                STOPFORM(TRUE,80,90)
                finished=TRUE
            FEND
            """, host);
        Assert.Equal<ScriptStopForm>([new(true, 80, 90), new(false)], host.Requests);
    }


    private sealed class Host : IScriptStopFormHost
    {
        internal readonly ConcurrentQueue<ScriptStopForm> Requests = new();
        internal readonly List<string> Output = [];
        internal Func<CancellationToken, Task>? Apply
        {
            get; init;
        }

        public async ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Apply is not null)
            {
                await Apply(token);
            }
            Requests.Enqueue(request);
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);
    }
}
