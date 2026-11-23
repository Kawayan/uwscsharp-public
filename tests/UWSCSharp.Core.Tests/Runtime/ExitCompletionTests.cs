using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ExitCompletionTests
{
    [Fact]
    public async Task HelpProcedureExitExampleRunsFinally()
    {
        Assert.Equal(["111", "333"], await Run("""
            関数1()
            PROCEDURE 関数1()
                TRY
                    PRINT "111"
                    EXIT
                    PRINT "222"
                FINALLY
                    PRINT "333"
                ENDTRY
                PRINT "444"
            FEND
            """));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("7", 7)]
    [InlineData("-3", -3)]
    [InlineData("1+6", 7)]
    [InlineData("\"7\"", 7)]
    [InlineData("\"7.9\"", 7)]
    [InlineData("7.9", 7)]
    [InlineData("-7.9", -7)]
    [InlineData("2147483647.9", int.MaxValue)]
    [InlineData("-2147483648.9", int.MinValue)]
    [InlineData("TRUE", 1)]
    [InlineData("FALSE", 0)]
    [InlineData("EMPTY", 0)]
    [InlineData("$FFFFFFFFFFFFFFFF", -1)]
    public async Task ExitCodeIsReturnedWithoutStoppingHost(
        string expression, int expected)
    {
        var output = new List<string>();
        int code = await Execute("PRINT 1\nEXITEXIT " + expression
            + "\nPRINT 1/0", output);
        Assert.Equal(expected, code);
        Assert.Equal(["1"], output);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("NULL")]
    [InlineData("\"bad\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1E999\"")]
    [InlineData("\"7<#NULL>8\"")]
    public async Task InvalidCodeHasSourceLineAndCanBeCaught(string expression)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Execute("// heading\nEXITEXIT " + expression, []));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught", "after"], await Run(
            "TRY\nEXITEXIT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\""));
    }

    [Theory]
    [InlineData("FOR i=1 TO 2", "NEXT")]
    [InlineData("FOR c IN \"AB\"", "NEXT")]
    [InlineData("WHILE TRUE", "WEND")]
    [InlineData("REPEAT", "UNTIL 1/0")]
    public async Task ExitLeavesRoutineAndSkipsLoopContinuation(
        string start, string end)
    {
        Assert.Equal(["9", "after"], await Run(
            "PRINT F()\nPRINT \"after\"\nFUNCTION F()\n" + start
            + "\nRESULT=9\nEXIT\nPRINT 1/0\n" + end
            + "\nRESULT=100\nFEND"));
    }

    [Fact]
    public async Task ExitCopiesBackAfterFinallyAndReturnsFinalResult()
    {
        Assert.Equal(["6", "8"], await Run("""
            x=1
            PRINT F(x)
            PRINT x
            FUNCTION F(VAR a)
                TRY
                    a=3
                    RESULT=4
                    EXIT
                FINALLY
                    a=8
                    RESULT=6
                ENDTRY
                PRINT 1/0
            FEND
            """));
    }

    [Fact]
    public async Task StopCodeIsEvaluatedOnceBeforeAllFinallyHandlers()
    {
        var output = new List<string>();
        int code = await Execute("""
            PUBLIC n=0
            TRY
                PRINT F()
            FINALLY
                PRINT "outer"
                PRINT n
            ENDTRY
            PRINT 1/0
            FUNCTION F()
                TRY
                    EXITEXIT Code()
                FINALLY
                    PRINT "inner"
                    n=n+10
                    EXIT
                ENDTRY
            FEND
            FUNCTION Code()
                n=n+1
                RESULT=7
            FEND
            """, output);
        Assert.Equal(7, code);
        Assert.Equal(["inner", "outer", "11"], output);
    }

    [Fact]
    public async Task StopIsNotCaughtByExceptAndSkipsReferenceCopyback()
    {
        var output = new List<string>();
        int code = await Execute("""
            PUBLIC n=1
            TRY
                TRY
                    F(n)
                EXCEPT
                    PRINT "caught"
                ENDTRY
            FINALLY
                PRINT n
            ENDTRY
            PROCEDURE F(VAR a)
                a=9
                EXITEXIT 7
            FEND
            """, output);
        Assert.Equal(7, code);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task CleanupStopReplacesPendingCode()
    {
        var output = new List<string>();
        Assert.Equal(9, await Execute(
            "TRY\nEXITEXIT 7\nFINALLY\nEXITEXIT 9\nENDTRY", output));
    }

    [Fact]
    public async Task CleanupFailureRemainsFailureInsteadOfExitCode()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Execute("TRY\nEXITEXIT 7\nFINALLY\nPRINT INT(NULL)\nENDTRY", []));
        Assert.Equal(4, error.Line);
    }

    [Theory]
    [InlineData("EXIT", 0)]
    [InlineData("PRINT 1", 0)]
    [InlineData("IF FALSE THEN EXITEXIT 9", 0)]
    public async Task NormalCompletionReturnsZero(string source, int expected)
    {
        Assert.Equal(expected, await Execute(source, []));
    }

    [Fact]
    public async Task RepeatedExecutionDoesNotShareExitCode()
    {
        var interpreter = new Interpreter();
        var host = new CallbackScriptHost(_ => { });
        Assert.Equal(7, await interpreter.RunWithExitCodeAsync("EXITEXIT 7",
            host, default, ScriptRunOptions.Empty));
        Assert.Equal(0, await interpreter.RunWithExitCodeAsync("EXIT",
            host, default, ScriptRunOptions.Empty));
        await interpreter.RunAsync("EXITEXIT 8", host);
    }

    [Theory]
    [InlineData("EXIT 7")]
    [InlineData("IF FALSE THEN EXIT 7")]
    [InlineData("IF FALSE THEN EXITEXIT 1+")]
    public void MalformedExitIsRejectedBeforeExecution(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }


    private static Task<int> Execute(string source, List<string> output) =>
        new Interpreter().RunWithExitCodeAsync(source,
            new CallbackScriptHost(output.Add), default, ScriptRunOptions.Empty);
}
