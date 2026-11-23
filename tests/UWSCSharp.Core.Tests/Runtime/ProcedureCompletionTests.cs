using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ProcedureCompletionTests
{
    [Theory]
    [InlineData("PROCEDURE", "P(Tick(),)")]
    [InlineData("FUNCTION", "P(Tick(),)")]
    [InlineData("PROCEDURE", "P(,Tick())")]
    [InlineData("FUNCTION", "P(,Tick())")]
    [InlineData("PROCEDURE", "P(Tick())")]
    [InlineData("FUNCTION", "P(Tick())")]
    [InlineData("PROCEDURE", "P(Tick(),Tick(),Tick())")]
    [InlineData("FUNCTION", "P(Tick(),Tick(),Tick())")]
    [InlineData("PROCEDURE", "P()")]
    [InlineData("FUNCTION", "P()")]
    public async Task InvalidArityIsRejectedBeforeAnyArgumentEvaluation(
        string declaration, string call)
    {
        Assert.Equal(["0"], await Run("PUBLIC ticks=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT ticks\nENDTRY\n" + declaration
            + " P(a,b)\nPRINT \"body\"\nFEND\nFUNCTION Tick()\n"
            + "ticks=ticks+1\nRESULT=ticks\nFEND"));
    }

    [Theory]
    [InlineData("PROCEDURE", "P(Tick(),)", "a,b=3", "1", "3", "1")]
    [InlineData("FUNCTION", "P(Tick(),)", "a,b=3", "1", "3", "1")]
    [InlineData("PROCEDURE", "P(,Tick())", "a=2,b=3", "2", "1", "1")]
    [InlineData("FUNCTION", "P(,Tick())", "a=2,b=3", "2", "1", "1")]
    [InlineData("PROCEDURE", "P(,)", "a=2,b=3", "2", "3", "0")]
    [InlineData("FUNCTION", "P(,)", "a=2,b=3", "2", "3", "0")]
    [InlineData("PROCEDURE", "P()", "a=2,b=3", "2", "3", "0")]
    [InlineData("FUNCTION", "P()", "a=2,b=3", "2", "3", "0")]
    [InlineData("PROCEDURE", "P(Tick(),Tick())", "a,b", "1", "2", "2")]
    [InlineData("FUNCTION", "P(Tick(),Tick())", "a,b", "1", "2", "2")]
    public async Task OptionalOmissionAndValidCallsKeepEvaluationOrder(
        string declaration, string call, string signature,
        string first, string second, string ticks)
    {
        Assert.Equal([first, second, ticks], await Run("PUBLIC ticks=0\n"
            + call + "\nPRINT ticks\n" + declaration + " P(" + signature + ")\n"
            + "PRINT a\nPRINT b\nFEND\nFUNCTION Tick()\nticks=ticks+1\nRESULT=ticks\nFEND"));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task ProcedureRecursionUsesExistingDepthBoundary(int depth, bool accepted)
    {
        var output = new List<string>();
        string source = "PUBLIC calls=0\nP(" + depth + ")\nPRINT calls\n"
            + "PROCEDURE P(n)\ncalls=calls+1\nIF n>1 THEN P(n-1)\nFEND";
        if (accepted)
        {
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal(["128"], output);
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(source, output.Add));
            Assert.Equal(6, error.Line);
            Assert.Empty(output);
        }
    }

    [Fact]
    public async Task ProcedureExpressionReturnsEmptyEvenWhenResultWasAssigned()
    {
        Assert.Equal(["0", "1"], await Run(
            "PRINT LENGTH(P())\nPRINT P()+1\nPROCEDURE P()\nRESULT=7\nFEND"));
    }

    [Fact]
    public async Task RequiredOmissionReportsCallLineWithoutEnteringBody()
    {
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(
                "// heading\nP(1,)\nPROCEDURE P(a,b)\nPRINT 99\nFEND", output.Add));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
    }
}
