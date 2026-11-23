using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class FinallyAuditTests
{
    private sealed class Files(Exception? cleanup) : IScriptFiles, IScriptFileSession
    {
        internal bool Created
        {
            get; private set;
        }

        internal bool Disposed
        {
            get; private set;
        }

        public IScriptFileSession CreateSession()
        {
            Created = true;
            return this;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) => ValueTask.FromResult(Value.Numeric(1));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return cleanup is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanup);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultCancellationSkipsScriptFinallyButDisposesSession(bool fails)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new IOException("cleanup");
        var files = new Files(fails ? cleanup : null);
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: files).RunAsync("""
                TRY
                    TRY
                        PRINT "body"
                    FINALLY
                        PRINT "inner"
                    ENDTRY
                FINALLY
                    PRINT "outer"
                ENDTRY
                """, text =>
            {
                output.Add(text);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Equal(["body"], output);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(files.Disposed);
        if (fails)
        {
            Assert.Equal([cleanup], Assert.IsType<AggregateException>(
                error.Data["UWSCSharp.CleanupErrors"]).InnerExceptions);
        }
    }

    [Fact]
    public async Task FinallyFailureReplacesPendingScriptErrorAndOuterFinallyRuns()
    {
        List<string> output = [];
        var files = new Files(null);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(files: files).RunAsync("""
                TRY
                    TRY
                        PRINT missing
                    FINALLY
                        PRINT 1/0
                    ENDTRY
                FINALLY
                    PRINT "outer"
                ENDTRY
                """, output.Add));
        Assert.Equal(5, error.Line);
        Assert.Equal(["outer"], output);
        Assert.True(files.Disposed);
    }

    [Fact]
    public async Task UnsupportedOptionIsRejectedBeforeCreatingSessions()
    {
        var files = new Files(null);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(files: files).RunAsync("OPTION OPTFINALLY, UNKNOWN\nPRINT 1", _ => { }));
        Assert.Equal(1, error.Line);
        Assert.False(files.Created);
    }
}
