using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ConstantDeclarationTests
{
    [Theory]
    [InlineData("CONST c=ABS(-7)", "7")]
    [InlineData("PUBLIC a=3\nCONST c=a+4\na=99", "7")]
    [InlineData("CONST a=3\nCONST c=POWER(a,2)", "9")]
    [InlineData("CONST c=ABS(INT(-7.9))", "7")]
    [InlineData("CONST c=COPY(\"abcd\",2,)", "bcd")]
    [InlineData("CONST c=JOIN(SPLIT(\"a,b\",\",\"),\"/\")", "a/b")]
    [InlineData("CONST c=FALSE AND TRUE", "0")]
    [InlineData("CONST c=EMPTY", "")]
    [InlineData("CONST c=NULL", "NULL")]
    [InlineData("CONST c=NOTHING", "NOTHING")]
    [InlineData("CONST c=$FFFFFFFFFFFFFFFF", "-1")]
    [InlineData("const c=7", "7")]
    [InlineData("CONST c=RANDOM(1)", "0")]
    [InlineData("CONST c=GETTIME(0,\"2000-01-01 00:00:01\")", "1")]
    public async Task InitializesScalarOnceBeforeMain(
        string source, string expected)
    {
        Assert.Equal([expected], await Run(source + "\nPRINT c"));
    }

    [Theory]
    [InlineData("IFB FALSE\nCONST c=7\nENDIF")]
    [InlineData("WHILE FALSE\nCONST c=7\nWEND")]
    [InlineData("FOR i=1 TO 0\nCONST c=7\nNEXT")]
    [InlineData("REPEAT\nCONST c=7\nUNTIL TRUE")]
    [InlineData("SELECT 0\nCASE 1\nCONST c=7\nSELEND")]
    [InlineData("TRY\nCONST c=7\nEXCEPT\nENDTRY")]
    [InlineData("TRY\nPRINT 1\nFINALLY\nCONST c=7\nENDTRY")]
    public async Task HoistsDeclarationsFromBlocks(string declaration)
    {
        string[] output = await Run("PRINT c\n" + declaration);
        Assert.Equal("7", output[0]);
    }

    [Fact]
    public async Task HoistsUncalledRoutineConstantAndInitializesInSourceOrder()
    {
        Assert.Equal(["9", "7"], await Run("""
            PRINT derived
            PRINT baseValue
            FUNCTION Uncalled()
                CONST baseValue=ABS(-7)
                RESULT=baseValue
            FEND
            PROCEDURE Other()
                CONST derived=baseValue+2
            FEND
            """));
    }

    [Fact]
    public async Task ReusedProgramDoesNotReusePublicSnapshots()
    {
        ScriptProgram program = ScriptCompiler.Compile("""
            PUBLIC a=3
            CONST c=a+4
            a=99
            PRINT c
            """);
        var interpreter = new Interpreter();
        for (int iteration = 0; iteration < 2; iteration++)
        {
            var host = new TestHost();
            await interpreter.RunAsync(program, host);
            Assert.Equal(["7"], host.Output);
        }
    }

    [Theory]
    [InlineData("CONST c", 1)]
    [InlineData("CONST c=", 1)]
    [InlineData("CONST c=1\nCONST C=2", 2)]
    [InlineData("CONST c=1\nPUBLIC C=2", 2)]
    [InlineData("CONST c=1\nFUNCTION F()\nCONST C=2\nFEND", 3)]
    [InlineData("CONST c[]=1,2", 1)]
    [InlineData("CONST c=F()\nFUNCTION F()\nRESULT=1\nFEND", 1)]
    [InlineData("CONST c=Unknown()", 1)]
    [InlineData("IFB FALSE\nPUBLIC c=1\nENDIF", 2)]
    [InlineData("PUBLIC c=Unknown()", 1)]
    public void InvalidDeclarationsFailBeforeExecution(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(
            () => ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("CONST c=ABS()")]
    [InlineData("CONST c=missing")]
    [InlineData("CONST c=c")]
    [InlineData("CONST c=1/0")]
    [InlineData("CONST c=SPLIT(\"a,b\",\",\")")]
    [InlineData("PUBLIC HASHTBL a\nCONST c=a")]
    public async Task InitializationFailurePreventsMainAndFinally(string declaration)
    {
        var host = new TestHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter().RunAsync(declaration + """

                TRY
                    PRINT "main"
                FINALLY
                    PRINT "finally"
                ENDTRY
                """, host));
        Assert.Equal(declaration.Split('\n').Length, error.Line);
        Assert.Empty(host.Output);
    }

    [Theory]
    [InlineData("CONST c=a\nCONST a=3", 1)]
    [InlineData("FUNCTION F(x)\nCONST c=x\nFEND", 2)]
    public async Task UninitializedNamesFailAtDeclaration(string source, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task InitializersCanSnapshotBuiltinOutputsAndArguments()
    {
        var host = new TestHost();
        await new Interpreter().RunAsync("""
            CONST t=GETTIME(0,"2000-01-01 00:00:01")
            CONST seconds=G_TIME_SS2
            CONST count=LENGTH(PARAM_STR)
            PRINT seconds
            PRINT count
            """, host, default, new ScriptRunOptions(["a", "b"]));
        Assert.Equal(["01", "2"], host.Output);
    }

    [Fact]
    public async Task AssignmentFailureKeepsConstantAndRunsFinally()
    {
        Assert.Equal(["finally", "7"], await Run("""
            CONST c=ABS(-7)
            TRY
                TRY
                    c=9
                FINALLY
                    PRINT "finally"
                ENDTRY
            EXCEPT
                PRINT c
            ENDTRY
            """));
    }

    [Fact]
    public async Task CancellationBeforeInitializationPreventsOutput()
    {
        ScriptProgram program = ScriptCompiler.Compile("CONST c=ABS(-7)\nPRINT c");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new TestHost();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(program, host, cancellation.Token));
        Assert.Empty(host.Output);
    }
}
