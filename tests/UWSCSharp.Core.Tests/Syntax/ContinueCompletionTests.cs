using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ContinueCompletionTests
{
    [Theory]
    [InlineData("BREAK")]
    [InlineData("CONTINUE")]
    public void NegativeHexadecimalCannotWrapToPositiveLevel(string keyword)
    {
        foreach (string level in new[]
        {
            "-$FFFFFFFF", "-xFFFFFFFE", "-0xFFFFFFFF",
            "$80000000", "$FFFFFFFF", "$100000000",
            "-$80000000", "-$100000000"
        })
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.Compile(
                    $"WHILE TRUE\nWHILE TRUE\n{keyword} {level}\nWEND\nWEND"));
            Assert.Equal(3, error.Line);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("$1")]
    [InlineData("x1")]
    [InlineData("0X1")]
    [InlineData("+$0001")]
    public async Task ContinuePreservesEveryLoopContinuation(string level)
    {
        Assert.Equal(["3", "3", "", "3"], await Run($"""
            FOR i=1 TO 2
                CONTINUE {level}
                PRINT "unreachable"
            NEXT
            PRINT i
            i=0
            WHILE i<3
                i=i+1
                CONTINUE {level}
                PRINT "unreachable"
            WEND
            PRINT i
            FOR c IN "AB"
                CONTINUE {level}
                PRINT "unreachable"
            NEXT
            PRINT c
            i=0
            REPEAT
                i=i+1
                CONTINUE {level}
                PRINT "unreachable"
            UNTIL i=3
            PRINT i
            """));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("$2")]
    [InlineData("x2")]
    [InlineData("0x2")]
    public async Task ContinueSkipsInnerUntilAndRunsOuterUntil(string level)
    {
        Assert.Equal(["inner", "outer", "inner", "outer", "2"],
            await Run($"""
                i=0
                REPEAT
                    i=i+1
                    TRY
                        REPEAT
                            TRY
                                SELECT i
                                CASE i
                                    CONTINUE {level}
                                SELEND
                            FINALLY
                                PRINT "inner"
                            ENDTRY
                        UNTIL 1/0
                    FINALLY
                        PRINT "outer"
                    ENDTRY
                UNTIL i=2
                PRINT i
                """));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("1+0")]
    [InlineData("n")]
    [InlineData("\"1\"")]
    [InlineData("1.0")]
    public void InvalidLevelsAreRejectedEvenInUnreachedBranch(string level)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(
                $"CONST n=1\nWHILE FALSE\nCONTINUE {level}\nWEND"));
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void ContinueCannotTargetCallingRoutineLoop()
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("FOR i=1 TO 2\nF()\nNEXT\n"
                + "PROCEDURE F()\nCONTINUE\nFEND"));
        Assert.Equal(5, error.Line);
    }

    [Theory]
    [InlineData(1, "         1")]
    [InlineData(10, "   3628800")]
    public async Task FactorialSampleRetriesInvalidInputs(
        int accepted, string formatted)
    {
        Assert.Equal(["number", "range", "range", formatted, "4"],
            await Run($$"""
                PUBLIC attempts=0
                PUBLIC chosen=0
                chosen={{accepted}}
                WHILE TRUE
                    value=ReadInput()
                    IFB VAL(value)=ERR_VALUE
                        PRINT "number"
                        CONTINUE
                    ENDIF
                    IFB value<1 OR value>10
                        PRINT "range"
                        CONTINUE
                    ENDIF
                    BREAK
                WEND
                PRINT FORMAT(Factorial(value),10)
                PRINT attempts
                FUNCTION ReadInput()
                    attempts=attempts+1
                    SELECT attempts
                    CASE 1
                        RESULT="bad"
                    CASE 2
                        RESULT=0
                    CASE 3
                        RESULT=11
                    DEFAULT
                        RESULT=chosen
                    SELEND
                FEND
                FUNCTION Factorial(value)
                    IFB value=0
                        RESULT=1
                        EXIT
                    ENDIF
                    RESULT=value*Factorial(value-1)
                FEND
                """));
    }
}
