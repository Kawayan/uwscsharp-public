using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DisplayInformationTests
{
    [Fact]
    public async Task VariablesAreReadLiveAndDuringGlobalInitialization()
    {
        var session = new DisplaySession();
        List<string> output = [];
        await new Interpreter(windows: new Automation(session)).RunAsync("""
            OPTION EXPLICIT
            PUBLIC width=G_SCREEN_W
            PRINT width
            PRINT g_mouse_x
            PRINT G_MOUSE_X
            PRINT G_MOUSE_Y
            PRINT G_SCREEN_H
            PRINT G_SCREEN_C
            """, output.Add);
        Assert.Equal(["100", "101", "102", "103", "104", "105"], output);
        Assert.Equal(6, session.Reads);
    }

    [Theory]
    [InlineData("G_MOUSE_X")]
    [InlineData("G_MOUSE_Y")]
    [InlineData("G_SCREEN_W")]
    [InlineData("G_SCREEN_H")]
    [InlineData("G_SCREEN_C")]
    public async Task ReadOnlyInformationAllowsLocalShadowing(string name)
    {
        var session = new DisplaySession();
        var runner = new Interpreter(windows: new Automation(session));
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            runner.RunAsync(name + "=1", _ => { }));
        Assert.Contains("読み取り専用", failure.Message);
        Assert.Equal(0, session.Reads);
        List<string> output = [];
        await runner.RunAsync($$"""
            DIM {{name}}="local"
            PRINT {{name}}
            Probe("parameter")
            PROCEDURE Probe({{name}})
                PRINT {{name}}
            FEND
            """, output.Add);
        Assert.Equal(["local", "parameter"], output);
        Assert.Equal(0, session.Reads);
    }

    [Theory]
    [InlineData("G_MOUSE_X")]
    [InlineData("G_MOUSE_Y")]
    [InlineData("G_SCREEN_W")]
    [InlineData("G_SCREEN_H")]
    [InlineData("G_SCREEN_C")]
    public async Task GlobalRedeclarationFailsBeforeInitializer(string name)
    {
        foreach (string declaration in new[] { "PUBLIC", "CONST" })
        {
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(
                    $"\n{declaration} {name}=1/0", _ => { }));
            Assert.Equal(2, failure.Line);
            Assert.Contains("再定義", failure.Message);
        }
    }

    [Theory]
    [InlineData("MONITOR(,Side())")]
    [InlineData("MONITOR(Side(),)")]
    [InlineData("MONITOR(Side(),0,0)")]
    public async Task InvalidCallShapeDoesNotEvaluateArguments(string call)
    {
        List<string> output = [];
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new Automation(new DisplaySession())).RunAsync(
                $$"""
                PRINT {{call}}
                FUNCTION Side()
                    PRINT "side"
                    RESULT=0
                FEND
                """, output.Add));
        Assert.Empty(output);
    }

    [Theory]
    [InlineData("MONITOR(0,999)")]
    [InlineData("MONITOR(1E100,0)")]
    [InlineData("MONITOR(0,1E100)")]
    public async Task InvalidValuesDoNotReachAdapter(string call)
    {
        var session = new DisplaySession();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new Automation(session))
                .RunAsync("PRINT " + call, _ => { }));
        Assert.Equal(0, session.Invocations);
    }

    [Fact]
    public async Task MonitorIsRegisteredAndValidCallsReachAdapter()
    {
        Assert.False(Names.CanDeclare("monitor"));
        var session = new DisplaySession();
        List<string> output = [];
        await new Interpreter(windows: new Automation(session)).RunAsync("""
            PRINT MONITOR()
            PRINT monitor(0,MON_NAME)
            PRINT MONITOR(0)
            """, output.Add);
        Assert.Equal(["2", "owned monitor", "owned monitor"], output);
        Assert.Equal(3, session.Invocations);
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("MONITOR()"));
    }


    [Fact]
    public async Task KnownFailuresAreCaughtButUnknownHostFailuresPropagate()
    {
        var failure = new InvalidOperationException("display failure");
        var session = new DisplaySession { Failure = failure, KnownFailure = true };
        List<string> output = [];
        await new Interpreter(windows: new Automation(session)).RunAsync("""
            TRY
                PRINT G_SCREEN_W
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, output.Add);
        Assert.Equal(["caught"], output);
        session.KnownFailure = false;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter(windows: new Automation(session))
                .RunAsync("PRINT G_MOUSE_X", _ => { }));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task CancellationAfterBackendReadIsNotSuppressed()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new DisplaySession { OnRead = cancellation.Cancel };
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: new Automation(session)).RunAsync(
                "PRINT G_SCREEN_W", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private sealed class Automation(DisplaySession session) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => session;
    }

    private sealed class DisplaySession : IWindowsAutomationSession
    {
        internal int Reads;
        internal int Invocations;
        internal Exception? Failure;
        internal bool KnownFailure;
        internal Action? OnRead;

        public Value ReadDisplayVariable(WindowsDisplayVariable variable,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            OnRead?.Invoke();
            return Value.Numeric(100 + Reads++);
        }

        public string? GetScriptErrorMessage(string name, Exception failure) =>
            KnownFailure && ReferenceEquals(Failure, failure)
                ? failure.Message : null;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Invocations++;
            return ValueTask.FromResult(arguments.Count == 0
                ? Value.Numeric(2) : Value.String("owned monitor"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
