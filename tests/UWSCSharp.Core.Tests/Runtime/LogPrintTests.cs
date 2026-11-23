using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class LogPrintTests
{
    [Fact]
    public async Task VisibilityBoundsOmissionsAndReturnValueAreAdopted()
    {
        var host = new ViewHost();
        await new Interpreter().RunAsync("""
            PRINT LOGPRINT(TRUE,-1.9,"20",640,320)
            LOGPRINT(FALSE)
            LOGPRINT(2,,,400,)
            """, host);
        Assert.Equal([""], host.Output);
        Assert.Equal(new ScriptLogView[]
        {
            new(true, -1, 20, 640, 320), new(false), new(true, Width: 400)
        }, host.Views);
    }

    [Theory]
    [InlineData("LOGPRINT()")]
    [InlineData("LOGPRINT(,)")]
    [InlineData("LOGPRINT(TRUE,1,2,3,4,Tick())")]
    public async Task InvalidShapeDoesNotEvaluateArguments(string call)
    {
        var host = new ViewHost();
        await new Interpreter().RunAsync("""
            PUBLIC ticks=0
            TRY
            """ + "\n" + call + "\n" + """
            EXCEPT
                PRINT ticks
            ENDTRY
            FUNCTION Tick()
                ticks=ticks+1
                RESULT=1
            FEND
            """, host);
        Assert.Equal(["0"], host.Output);
        Assert.Empty(host.Views);
    }

    [Theory]
    [InlineData("LOGPRINT(NULL)")]
    [InlineData("LOGPRINT(NOTHING)")]
    [InlineData("LOGPRINT(\"bad\")")]
    [InlineData("LOGPRINT(TRUE,2147483648)")]
    [InlineData("LOGPRINT(TRUE,-2147483649)")]
    [InlineData("LOGPRINT(TRUE,,,0)")]
    [InlineData("LOGPRINT(TRUE,,,-1)")]
    [InlineData("LOGPRINT(TRUE,,,,0.5)")]
    public async Task InvalidValuesReportCallLineWithoutChangingView(string call)
    {
        var host = new ViewHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + call, host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Views);
    }

    [Fact]
    public async Task InvalidEarlierValueStopsLaterArgumentSideEffect()
    {
        var host = new ViewHost();
        await new Interpreter().RunAsync("""
            PUBLIC ticks=0
            TRY
                LOGPRINT(TRUE,Tick(),,0,Tick())
            EXCEPT
                PRINT ticks
            ENDTRY
            FUNCTION Tick()
                ticks=ticks+1
                RESULT=ticks
            FEND
            """, host);
        Assert.Equal(["1"], host.Output);
        Assert.Empty(host.Views);
    }

    [Fact]
    public async Task UnsupportedHostRejectsBeforeFlagEvaluation()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("LOGPRINT(1/0)", _ => { }));
        Assert.Contains("ログウィンドウ", error.Message);
    }


    [Fact]
    public async Task ViewFailureKeepsIdentityAndBypassesScriptCatch()
    {
        var failure = new IOException("view failed");
        var host = new ViewHost { Failure = failure };
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter().RunAsync("TRY\nLOGPRINT(TRUE)\nEXCEPT\nPRINT 1\nENDTRY", host));
        Assert.Same(failure, error);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task CancellationAfterViewApplicationPreventsNextStatement()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new ViewHost { Applied = cancellation.Cancel };
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("LOGPRINT(TRUE)\nPRINT 1", host, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(host.Views);
        Assert.Empty(host.Output);
    }

    private sealed class ViewHost : IScriptLogViewHost
    {
        internal List<string> Output { get; } = [];

        internal List<ScriptLogView> Views { get; } = [];

        internal Exception? Failure
        {
            get; init;
        }

        internal Action? Applied
        {
            get; init;
        }

        public ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Views.Add(view);
            Applied?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask SetLogHistoryLimitAsync(int maximumLines, CancellationToken token) =>
            ValueTask.CompletedTask;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
