using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class BreakCompletionTests
{
    public static IEnumerable<object[]> LoopPairs()
    {
        string[] kinds = ["for", "for-in", "while", "repeat"];
        foreach (string outer in kinds)
        {
            foreach (string inner in kinds)
            {
                yield return [outer, inner];
            }
        }
    }

    [Theory]
    [MemberData(nameof(LoopPairs))]
    public async Task TwoLevelsExitBothLoopsWithoutTheirContinuation(
        string outer, string inner)
    {
        (string start, string end) = Loop(outer, false);
        (string nested, string finish) = Loop(inner, true);
        Assert.Equal(["1"], await Run(
            "n=0\n" + start + "\n" + nested
            + "\nn=n+1\nBREAK 2\nPRINT 1/0\n" + finish
            + "\nPRINT 1/0\n" + end + "\nPRINT n"));
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData("+1", "1")]
    [InlineData("01", "1")]
    [InlineData("$1", "1")]
    [InlineData("x1", "1")]
    [InlineData("0X1", "1")]
    [InlineData("+$0001", "1")]
    public async Task BreakDoesNotIncrementForCounter(
        string level, string expected)
    {
        Assert.Equal([expected], await Run(
            $"FOR i=1 TO 2\nBREAK {level}\nNEXT\nPRINT i"));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("$2")]
    [InlineData("x2")]
    [InlineData("0x2")]
    public async Task MultiLevelBreakPreservesBothForInElements(string level)
    {
        Assert.Equal(["A", "C"], await Run(
            $"FOR c IN \"AB\"\nFOR d IN \"CD\"\nBREAK {level}"
            + "\nNEXT\nNEXT\nPRINT c\nPRINT d"));
    }

    [Fact]
    public async Task ThreeLevelsUnwindFinallyInOrder()
    {
        Assert.Equal(["inner", "middle", "outer", "1"], await Run("""
            FOR i=1 TO 2
                TRY
                    WHILE TRUE
                        TRY
                            REPEAT
                                TRY
                                    SELECT i
                                    CASE 1
                                        BREAK 3
                                    SELEND
                                FINALLY
                                    PRINT "inner"
                                ENDTRY
                            UNTIL 1/0
                        FINALLY
                            PRINT "middle"
                        ENDTRY
                    WEND
                FINALLY
                    PRINT "outer"
                ENDTRY
            NEXT
            PRINT i
            """));
    }

    [Fact]
    public async Task FinallyContinueReplacesPendingBreak()
    {
        Assert.Equal(["1", "2", "3"], await Run("""
            FOR i=1 TO 2
                TRY
                    BREAK
                FINALLY
                    PRINT i
                    CONTINUE
                ENDTRY
            NEXT
            PRINT i
            """));
    }

    [Fact]
    public async Task ExceptCanBreakAndFinallyStillRuns()
    {
        Assert.Equal(["cleanup", "1"], await Run("""
            FOR i=1 TO 2
                TRY
                    TRY
                        PRINT INT(NULL)
                    EXCEPT
                        BREAK
                    ENDTRY
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            NEXT
            PRINT i
            """));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("depth")]
    [InlineData("1+1")]
    [InlineData("(2)")]
    [InlineData("2.0")]
    [InlineData("\"2\"")]
    [InlineData("-$FFFFFFFF")]
    [InlineData("$80000000")]
    [InlineData("2147483648")]
    public void InvalidLevelIsCheckedBeforeUnreachedBranch(string level)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("CONST depth=2\nFOR i=1 TO 2\n"
                + $"WHILE FALSE\nBREAK {level}\nWEND\nNEXT"));
        Assert.Equal(4, error.Line);
    }

    [Theory]
    [InlineData("FUNCTION F()")]
    [InlineData("PROCEDURE F()")]
    public void BreakCannotExitCallingRoutineLoop(string declaration)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("FOR i=1 TO 2\nF()\nNEXT\n"
                + declaration + "\nBREAK\nFEND"));
        Assert.Equal(5, error.Line);
    }

    [Fact]
    public async Task RoutineLocalBreakDoesNotAffectCallerLoop()
    {
        Assert.Equal(["1", "1", "2", "1"], await Run("""
            FOR i=1 TO 2
                PRINT i
                F()
            NEXT
            PROCEDURE F()
                FOR j=1 TO 2
                    BREAK
                NEXT
                PRINT j
            FEND
            """));
    }

    private static (string Start, string End) Loop(
        string kind, bool inner)
    {
        string counter = inner ? "j" : "i";
        string item = inner ? "d" : "c";
        return kind switch
        {
            "for" => ($"FOR {counter}=1 TO 2", "NEXT"),
            "for-in" => ($"FOR {item} IN \"AB\"", "NEXT"),
            "while" => ($"{counter}=0\nWHILE {counter}<2\n"
                + $"{counter}={counter}+1", "WEND"),
            "repeat" => ("REPEAT", "UNTIL 1/0"),
            _ => throw new ArgumentException("不明なループ種別です。", nameof(kind))
        };
    }
}
