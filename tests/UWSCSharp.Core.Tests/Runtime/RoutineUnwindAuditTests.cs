using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class RoutineUnwindAuditTests
{
    private sealed class Files(List<string> events) : IScriptFiles, IScriptFileSession
    {
        public IScriptFileSession CreateSession()
        {
            events.Add("create");
            return this;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) => ValueTask.FromResult(Value.Numeric(1));

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task LateCopybackFailureKeepsFormalOrderAndRunsCallerFinally(string kind)
    {
        List<string> events = [];
        await new Interpreter(files: new Files(events)).RunAsync("""
            PUBLIC row=0
            DIM values[0]=1
            first=1
            TRY
                TRY
                    Change(first,values[row])
                EXCEPT
                    PRINT TRY_ERRLINE
                ENDTRY
            FINALLY
                PRINT "caller"
            ENDTRY
            PRINT first
            PRINT values[0]
            """ + "\n" + kind + """
             Change(VAR x,VAR y)
                TRY
                    x=9
                    y=8
                    row=1
                FINALLY
                    PRINT "callee"
                ENDTRY
            FEND
            """, events.Add);
        Assert.Equal(["create", "callee", "6", "caller", "9", "1", "dispose"], events);
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task CancellationDuringCopybackKeepsCompletedWritesAndDisposes(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        List<string> events = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(files: new Files(events)).RunAsync("""
                OPTION OPTFINALLY
                PUBLIC calls=0
                DIM values[0]=1
                first=1
                TRY
                    Change(first,values[NextIndex()])
                FINALLY
                    PRINT first
                    PRINT values[0]
                ENDTRY
                """ + "\n" + kind + """
                 Change(VAR x,VAR y)
                    TRY
                        x=9
                        y=8
                    FINALLY
                        PRINT "callee"
                    ENDTRY
                FEND
                FUNCTION NextIndex()
                    calls=calls+1
                    IF calls=2 THEN PRINT "trigger"
                    RESULT=0
                FEND
                """, text =>
            {
                events.Add(text);
                if (text == "trigger")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["create", "callee", "trigger", "9", "1", "dispose"], events);
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task CalleeFinallyFailurePreventsCopyback(string kind)
    {
        List<string> events = [];
        await new Interpreter(files: new Files(events)).RunAsync("""
            first=1
            TRY
                Change(first)
            EXCEPT
                PRINT first
            ENDTRY
            """ + "\n" + kind + """
             Change(VAR x)
                TRY
                    x=9
                FINALLY
                    PRINT 1/0
                ENDTRY
            FEND
            """, events.Add);
        Assert.Equal(["create", "1", "dispose"], events);
    }
}
