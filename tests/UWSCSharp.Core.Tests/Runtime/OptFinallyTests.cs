using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class OptFinallyTests
{
    private sealed class Services : IScriptFiles, IScriptFileSession
    {
        internal List<string> Events { get; } = [];

        internal Exception? CleanupFailure
        {
            get; init;
        }

        public IScriptFileSession CreateSession()
        {
            Events.Add("create");
            return this;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add(name);
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return CleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CleanupFailure);
        }
    }

    private sealed class Host(CancellationTokenSource cancellation, List<string> events,
        string cancelOn = "body") : IScriptHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            events.Add(text);
            if (text == cancelOn)
            {
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInsideActiveFinallyDoesNotRestartIt(bool enabled)
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: services).RunAsync((enabled ? "OPTION OPTFINALLY\n" : "") + """
                TRY
                    TRY
                        PRINT "start"
                    FINALLY
                        PRINT "trigger"
                        PRINT "inner unreachable"
                    ENDTRY
                FINALLY
                    PRINT "outer"
                ENDTRY
                """, new Host(cancellation, services.Events, "trigger"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(enabled ? ["create", "start", "trigger", "outer", "dispose"]
            : ["create", "start", "trigger", "dispose"], services.Events);
    }

    [Theory]
    [InlineData("EXIT")]
    [InlineData("EXITEXIT")]
    [InlineData("EXITEXIT 7")]
    public async Task CleanupExitCannotConvertPendingCancellationToSuccess(string exit)
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: services).RunAsync(
                "OPTION OPTFINALLY\nTRY\nPRINT \"body\"\nFINALLY\n" + exit + "\nENDTRY",
                new Host(cancellation, services.Events), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["create", "body", "dispose"], services.Events);
        if (exit.StartsWith("EXITEXIT", StringComparison.Ordinal))
        {
            Assert.Single(Assert.IsType<AggregateException>(
                error.Data["UWSCSharp.FinallyErrors"]).InnerExceptions);
        }
    }

    [Theory]
    [InlineData("OPTION OPTFINALLY")]
    [InlineData("option optfinally")]
    [InlineData("OPTION OPTFINALLY, OPTFINALLY")]
    [InlineData("OPTION OPTFINALLY\nOPTION OPTFINALLY")]
    public async Task CancellationUnwindsNestedFinallyBeforeDisposingAndRemainsCancelled(string option)
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: services).RunAsync(option + """

                TRY
                    TRY
                        PRINT "body"
                    FINALLY
                        FCLOSE(1)
                        PRINT "inner"
                    ENDTRY
                FINALLY
                    PRINT "outer"
                ENDTRY
                PRINT "unreachable"
                """, new Host(cancellation, services.Events), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["create", "body", "FCLOSE", "inner", "outer", "dispose"], services.Events);
    }

    [Fact]
    public async Task OptionDoesNotLeakIntoSubsequentDefaultProgram()
    {
        var runner = new Interpreter();
        foreach (bool enabled in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            List<string> events = [];
            string script = (enabled ? "OPTION OPTFINALLY\n" : "")
                + "TRY\nPRINT \"body\"\nFINALLY\nPRINT \"cleanup\"\nENDTRY";
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                script, new Host(cancellation, events), cancellation.Token));
            Assert.Equal(enabled ? ["body", "cleanup"] : ["body"], events);
        }
    }

    [Theory]
    [InlineData("OPTION")]
    [InlineData("OPTION OPTFINALLY,")]
    [InlineData("OPTION UNKNOWN")]
    [InlineData("PRINT 1\nOPTION OPTFINALLY")]
    [InlineData("IF TRUE\nOPTION OPTFINALLY\nENDIF")]
    public void UnsupportedOrMisplacedOptionsFailCompilation(string script)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(script));
    }

    [Fact]
    public async Task CleanupScriptFailureAndSessionFailureDoNotHideCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new IOException("dispose failure");
        var services = new Services { CleanupFailure = cleanup };
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: services).RunAsync("""
                OPTION OPTFINALLY
                TRY
                    PRINT "body"
                FINALLY
                    PRINT missing
                ENDTRY
                """, new Host(cancellation, services.Events), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AggregateException final = Assert.IsType<AggregateException>(error.Data["UWSCSharp.FinallyErrors"]);
        Assert.Equal(5, Assert.IsType<ScriptException>(Assert.Single(final.InnerExceptions)).Line);
        Assert.Equal([cleanup], Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.CleanupErrors"]).InnerExceptions);
        Assert.Equal(["create", "body", "dispose"], services.Events);
    }

    [Theory]
    [InlineData("WHILE TRUE\nWEND", typeof(InvalidOperationException))]
    [InlineData("SLEEP(1)", typeof(OperationCanceledException))]
    public async Task NonterminatingCleanupIsBoundedAndSessionStillDisposes(string cleanup, Type expected)
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services();
        Task run = new Interpreter(files: services).RunAsync(
            "OPTION OPTFINALLY\nTRY\nPRINT \"body\"\nFINALLY\n" + cleanup + "\nENDTRY",
            new Host(cancellation, services.Events), cancellation.Token);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Exception failure = Assert.Single(Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.FinallyErrors"]).InnerExceptions);
        Assert.IsAssignableFrom(expected, failure);
        Assert.Equal("dispose", services.Events[^1]);
    }

    [Fact]
    public async Task NestedUnwindSharesItsCleanupBudget()
    {
        using var cancellation = new CancellationTokenSource();
        var services = new Services();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: services).RunAsync("""
                OPTION OPTFINALLY
                TRY
                    TRY
                        PRINT "body"
                    FINALLY
                        WHILE TRUE
                        WEND
                    ENDTRY
                FINALLY
                    PRINT "outer"
                ENDTRY
                """, new Host(cancellation, services.Events), cancellation.Token));
        Assert.Equal(["create", "body", "dispose"], services.Events);
        Assert.Equal(2, Assert.IsType<AggregateException>(
            error.Data["UWSCSharp.FinallyErrors"]).InnerExceptions.Count);
    }

    [Fact]
    public async Task ReusedProgramKeepsCleanupStateIndependentBetweenConcurrentRuns()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            OPTION OPTFINALLY
            TRY
                PRINT "body"
            FINALLY
                PRINT "cleanup"
            ENDTRY
            """);
        var runner = new Interpreter();
        async Task Run()
        {
            using var cancellation = new CancellationTokenSource();
            List<string> events = [];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                program, new Host(cancellation, events), cancellation.Token));
            Assert.Equal(["body", "cleanup"], events);
        }

        await Task.WhenAll(Run(), Run());
    }
}
