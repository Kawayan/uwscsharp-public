using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ThreadRuntimeCompletionTests
{
    [Fact]
    public async Task RejectsTheSixtyFifthLiveWorkerAndJoinsAllStartedWorkers()
    {
        string source = string.Concat(Enumerable.Repeat("THREAD Worker()\n", 65))
            + "PROCEDURE Worker()\nSLEEP(30)\nFEND\n";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(source, _ => { }, timeout.Token));
        Assert.Equal(65, failure.Line);
        Assert.Contains("64", failure.Message);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharesPublicAndCopiesArgumentsWithoutVarWriteback(bool reference)
    {
        // PUBLICの複数名構文と切り離して実行境界を検査します。
        string source = "PUBLIC ready=0\nPUBLIC release=0\nPUBLIC done=0\nPUBLIC shared=10\n"
            + "DIM argument=10\nTHREAD Worker(argument)\n"
            + "WHILE ready=0\nSLEEP(0.001)\nWEND\n"
            + "argument=12\nshared=12\nrelease=1\n"
            + "WHILE done=0\nSLEEP(0.001)\nWEND\nPRINT argument\nPRINT shared\n"
            + $"PROCEDURE Worker({(reference ? "VAR " : "")}value)\n"
            + "ready=1\nWHILE release=0\nSLEEP(0.001)\nWEND\n"
            + "PRINT value\nPRINT shared\nvalue=30\nshared=30\ndone=1\nFEND";
        ConcurrentQueue<string> output = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync(source, output.Enqueue, timeout.Token);
        Assert.Equal(["10", "12", "12", "30"], output);
    }

    [Fact]
    public async Task DedicatedWorkerKeepsItsThreadAcrossAwaits()
    {
        ConcurrentQueue<(string Text, int Thread)> events = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync("""
            PUBLIC done=0
            PRINT "main"
            THREAD Worker()
            WHILE done=0
                SLEEP(0.001)
            WEND
            PROCEDURE Worker()
                PRINT "worker-before"
                SLEEP(0.001)
                PRINT "worker-after"
                done=1
            FEND
            """, text => events.Enqueue((text, Environment.CurrentManagedThreadId)),
            timeout.Token);
        (string Text, int Thread)[] result = [.. events];
        Assert.Equal(["main", "worker-before", "worker-after"],
            result.Select(row => row.Text));
        Assert.NotEqual(result[0].Thread, result[1].Thread);
        Assert.Equal(result[1].Thread, result[2].Thread);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentCompletionCancelsWorkersAndWaitsForTheirFinally(bool finallyEnabled)
    {
        ConcurrentQueue<string> output = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync((finallyEnabled ? "OPTION OPTFINALLY\n" : "") + """
            PUBLIC ready=0
            THREAD Worker()
            WHILE ready=0
                SLEEP(0.001)
            WEND
            PROCEDURE Worker()
                TRY
                    ready=1
                    SLEEP(30)
                FINALLY
                    PRINT "worker-cleanup"
                ENDTRY
            FEND
            """, output.Enqueue, timeout.Token);
        Assert.Equal(finallyEnabled ? new[] { "worker-cleanup" } : [], output);
    }

    [Fact]
    public async Task ControlsSuspendResumeAndQuitAtStatementBoundaries()
    {
        ConcurrentQueue<string> output = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync("""
            OPTION OPTFINALLY
            PUBLIC ready=0
            PUBLIC count=0
            PUBLIC done=0
            THREAD Worker()
            WHILE ready=0
                SLEEP(0.001)
            WEND
            CTRLTRD(0,TRD_SUSPEND)
            SLEEP(0.05)
            DIM previous=count
            SLEEP(0.05)
            PRINT count=previous
            CTRLTRD(0,TRD_RESUME)
            WHILE count=previous
                SLEEP(0.001)
            WEND
            CTRLTRD(0,TRD_QUIT)
            WHILE done=0
                SLEEP(0.001)
            WEND
            PRINT done
            PROCEDURE Worker()
                TRY
                    ready=1
                    WHILE TRUE
                        count=count+1
                        SLEEP(0.001)
                    WEND
                FINALLY
                    done=1
                ENDTRY
            FEND
            """, output.Enqueue, timeout.Token);
        Assert.Equal(["1", "1"], output);
    }

    [Fact]
    public async Task WorkerFailureInterruptsParentWaitAndPreservesSourceFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("""
                THREAD Worker()
                SLEEP(30)
                PROCEDURE Worker()
                    PRINT 1/0
                FEND
                """, _ => { }, timeout.Token));
        Assert.Equal(4, failure.Line);
    }

    [Fact]
    public async Task WorkerExitExitStopsWholeRunWithItsExitCode()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int code = await new Interpreter().RunWithExitCodeAsync(
            ScriptCompiler.Compile("""
                THREAD Worker()
                SLEEP(30)
                PROCEDURE Worker()
                    EXITEXIT 37
                FEND
                """), new CallbackScriptHost(_ => { }), timeout.Token,
            ScriptRunOptions.Empty);
        Assert.Equal(37, code);
    }

    [Fact]
    public async Task EvalStartsWorkerAndReturnsAnEmptyString()
    {
        ConcurrentQueue<string> output = new();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter().RunAsync("""
            PUBLIC done=0
            PRINT EVAL("THREAD Worker()")=""
            WHILE done=0
                SLEEP(0.001)
            WEND
            PROCEDURE Worker()
                done=1
            FEND
            """, output.Enqueue, timeout.Token);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task ShutdownRejectsWorkerCreationFromCancelledFinally()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter().RunAsync("""
                OPTION OPTFINALLY
                PUBLIC ready=0
                THREAD Worker()
                WHILE ready=0
                    SLEEP(0.001)
                WEND
                PROCEDURE Worker()
                    TRY
                        ready=1
                        SLEEP(30)
                    FINALLY
                        THREAD Nested()
                    ENDTRY
                FEND
                PROCEDURE Nested()
                    SLEEP(30)
                FEND
                """, _ => { }, timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(failure.Data.Contains("UWSCSharp.FinallyErrors"));
    }
}
