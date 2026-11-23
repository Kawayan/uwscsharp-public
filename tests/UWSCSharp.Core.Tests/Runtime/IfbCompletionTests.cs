using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class IfbCompletionTests
{
    [Theory]
    [InlineData("IF TRUE")]
    [InlineData("IF TRUE THEN")]
    [InlineData("IFB TRUE")]
    [InlineData("IFB TRUE THEN")]
    public async Task BlockFormsSelectOnlyFirstMatch(string opening)
    {
        Assert.Equal(["yes"], await Run(opening
            + "\nPRINT \"yes\"\nELSEIF INT(NULL)\nPRINT 2\nELSE\nPRINT 3\nENDIF"));
    }

    [Fact]
    public async Task ConditionsRunOnceUntilFirstMatch()
    {
        Assert.Equal(["22"], await Run("""
            PUBLIC n=0
            IFB Tick()=0
                n=n+10
            ELSEIF Tick()=2 THEN
                n=n+20
            ELSEIF Tick()=3
                n=n+30
            ENDIF
            PRINT n
            FUNCTION Tick()
                n=n+1
                RESULT=n
            FEND
            """));
    }

    [Theory]
    [InlineData("IFB TRUE\nELSEIF INT(NULL)\nPRINT 9\nENDIF")]
    [InlineData("IFB FALSE\nPRINT 9\nENDIF")]
    [InlineData("IFB FALSE\nPRINT 9\nELSE\nENDIF")]
    public async Task EmptySelectedBodyAndAbsentElseProduceNoOutput(string source)
    {
        Assert.Empty(await Run(source));
    }

    [Theory]
    [InlineData("0", "else")]
    [InlineData("EMPTY", "else")]
    [InlineData("-0.5", "then")]
    [InlineData("\"$FF\"", "then")]
    public async Task ElseIfUsesCommonNumericCondition(string condition, string expected)
    {
        Assert.Equal([expected], await Run("IFB FALSE\nPRINT 0\nELSEIF "
            + condition + "\nPRINT \"then\"\nELSE\nPRINT \"else\"\nENDIF"));
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("\"TRUE\"")]
    [InlineData("\"bad\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e999\"")]
    public async Task ElseIfConversionFailureKeepsItsOwnLine(string condition)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("IFB FALSE\nPRINT 0\nELSEIF " + condition + "\nPRINT 1\nENDIF"));
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public async Task NestedElseAndInlineElseBelongToTheirOwnCondition()
    {
        Assert.Equal(["inner", "inline"], await Run("""
            IFB TRUE
                IFB FALSE
                    PRINT 1
                ELSE
                    PRINT "inner"
                ENDIF
                IF FALSE THEN PRINT 2 ELSE PRINT "inline"
            ELSE
                PRINT 3
            ENDIF
            """));
    }

    [Theory]
    [InlineData("BREAK", "1")]
    [InlineData("CONTINUE", "2")]
    public async Task BranchFlowAndFinallyKeepEnclosingLoop(string flow, string count)
    {
        Assert.Equal(["cleanup", count], await Run("n=0\nFOR i=1 TO 3\n"
            + "IFB i=2\nTRY\n" + flow
            + "\nFINALLY\nPRINT \"cleanup\"\nENDTRY\nENDIF\nn=n+1\nNEXT\nPRINT n"));
    }

    [Fact]
    public async Task RoutineExitRunsFinallyAndReturnsResult()
    {
        Assert.Equal(["cleanup", "7"], await Run("""
            PRINT F()
            FUNCTION F()
                TRY
                    IFB TRUE
                        RESULT=7
                        EXIT
                    ENDIF
                    PRINT INT(NULL)
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            FEND
            """));
    }

    [Fact]
    public async Task UnselectedConstStillUsesDeclarationInitialization()
    {
        Assert.Equal(["7"], await Run(
            "IFB FALSE\nCONST c=ABS(-7)\nPRINT INT(NULL)\nENDIF\nPRINT c"));
    }

    [Fact]
    public async Task SelectedBodyAndConditionCalleeKeepOriginalErrorLine()
    {
        ScriptException body = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("IFB TRUE\nPRINT INT(NULL)\nENDIF"));
        Assert.Equal(2, body.Line);
        ScriptException callee = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("IFB FALSE\nELSEIF Bad()\nENDIF\nFUNCTION Bad()\nRESULT=INT(NULL)\nFEND"));
        Assert.Equal(5, callee.Line);
    }

    [Theory]
    [InlineData("IFB\nENDIF", 1)]
    [InlineData("IFB TRUE", 1)]
    [InlineData("IFB TRUE\nELSE 1\nENDIF", 2)]
    [InlineData("IFB TRUE\nELSE\nELSE\nENDIF", 3)]
    [InlineData("IFB TRUE\nELSE\nELSEIF TRUE\nENDIF", 3)]
    [InlineData("IFB FALSE\nELSEIF\nENDIF", 2)]
    [InlineData("IFB TRUE THEN PRINT 1", 1)]
    [InlineData("IFB FALSE\nELSEIF TRUE THEN PRINT 1\nENDIF", 2)]
    [InlineData("IFB TRUE\nENDIF 1", 2)]
    [InlineData("IFB TRUE\nWEND", 2)]
    [InlineData("IFB TRUE\nELSEIF FALSE\nPRINT 1+\nENDIF", 3)]
    [InlineData("ELSE", 1)]
    public void InvalidStructureIsRejectedBeforeExecution(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void BlockDepthUsesCommonStatementLimit(int depth, bool accepted)
    {
        string source = string.Concat(Enumerable.Repeat("IFB TRUE\n", depth))
            + string.Concat(Enumerable.Repeat("ENDIF\n", depth));
        if (accepted)
        {
            ScriptCompiler.Compile(source);
        }
        else
        {
            Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
        }
    }

    [Fact]
    public async Task CancellationDuringElseIfDoesNotRunBody()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                IFB FALSE
                    PRINT 0
                ELSEIF Tick()
                    PRINT "body"
                ENDIF
                FUNCTION Tick()
                    PRINT "condition"
                    RESULT=1
                FEND
                """, text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }, cancellation.Token));
        Assert.Equal(["condition"], output);
    }
}
