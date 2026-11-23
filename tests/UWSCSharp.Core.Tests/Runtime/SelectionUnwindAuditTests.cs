using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SelectionUnwindAuditTests
{
    [Theory]
    [InlineData("\"2\"", "2", "match")]
    [InlineData("2", "\"2\"", "match")]
    [InlineData("\"AbC\"", "\"abc\"", "match")]
    [InlineData("EMPTY", "0", "default")]
    [InlineData("0", "EMPTY", "default")]
    [InlineData("EMPTY", "EMPTY", "match")]
    public async Task SelectionUsesAdoptedComparisonRules(string selector,
        string candidate, string expected)
    {
        Assert.Equal([expected], await Run(
            $"SELECT {selector}\nCASE {candidate}\nPRINT \"match\"\nDEFAULT\nPRINT \"default\"\nSELEND"));
    }

    [Fact]
    public async Task SelectionStopsCaseEvaluationAtFirstMatch()
    {
        Assert.Equal(["selected", "11"], await Run("""
            PUBLIC n=0
            SELECT Selector()
            CASE Candidate(),1/0
                PRINT "selected"
            CASE 1/0
                PRINT "unreachable"
            SELEND
            PRINT n
            FUNCTION Selector()
                n=n+1
                RESULT="2"
            FEND
            FUNCTION Candidate()
                n=n+10
                RESULT=2
            FEND
            """));
    }

    [Fact]
    public async Task SelectionComparisonFailureRunsFinallyBeforeExcept()
    {
        Assert.Equal(["finally", "4"], await Run("""
            TRY
                TRY
                    SELECT 2
                    CASE "bad"
                        PRINT "unreachable"
                    SELEND
                FINALLY
                    PRINT "finally"
                ENDTRY
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            """));
    }

    [Theory]
    [InlineData("BREAK 2", "1", "outer1", "done")]
    [InlineData("CONTINUE 2", "1", "outer1", "2", "outer2", "done")]
    public async Task NestedJumpUnwindsFinallyWithoutCountingSelect(
        string jump, params string[] expected)
    {
        Assert.Equal(expected, await Run(
            "FOR i=1 TO 2\nTRY\nWHILE TRUE\nTRY\nSELECT i\nCASE i\n"
            + jump + "\nSELEND\nFINALLY\nPRINT i\nENDTRY\nWEND\n"
            + "FINALLY\nPRINT \"outer\"+i\nENDTRY\nNEXT\nPRINT \"done\""));
    }

    [Theory]
    [InlineData("BREAK")]
    [InlineData("CONTINUE")]
    public async Task FinallyFailureReplacesPendingJump(string jump)
    {
        Assert.Equal(["caught", "1"], await Run(
            "TRY\nFOR i=1 TO 2\nTRY\n" + jump
            + "\nFINALLY\nPRINT 1/0\nENDTRY\nNEXT\nEXCEPT\n"
            + "PRINT \"caught\"\nENDTRY\nPRINT i"));
    }

    [Theory]
    [InlineData("BREAK 2")]
    [InlineData("CONTINUE 2")]
    public async Task CancellationDuringJumpCleanupRunsPendingOuterFinally(string jump)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new TestHost
        {
            OnOutput = () => cancellation.Cancel()
        };
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(
                "OPTION OPTFINALLY\nFOR i=1 TO 2\nTRY\nWHILE TRUE\nTRY\n"
                + jump + "\nFINALLY\nPRINT \"inner\"\nPRINT \"unreachable\"\nENDTRY\nWEND\n"
                + "FINALLY\nPRINT \"outer\"\nENDTRY\nNEXT", host, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["inner", "outer"], host.Output);
    }

    [Theory]
    [InlineData("BREAK 0")]
    [InlineData("CONTINUE -1")]
    [InlineData("BREAK 2")]
    [InlineData("CONTINUE 1.5")]
    [InlineData("BREAK \"1\"")]
    [InlineData("CONTINUE $100000000")]
    [InlineData("BREAK n")]
    public void JumpLevelIsRestrictedToPositiveDecimalLiteral(string jump)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("FOR i=1 TO 2\n" + jump + "\nNEXT"));
        Assert.Equal(2, error.Line);
    }
}
