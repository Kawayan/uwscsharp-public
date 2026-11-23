using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class TextBlockWithShortCircuitTests
{
    private static async Task<string[]> Run(string script)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(script, output.Add);
        return [.. output];
    }

    [Fact]
    public async Task TextBlockPreservesRawBodyAndPhysicalLineNumbers()
    {
        Assert.Equal(["  first // raw ; _\r\n    second \"x\"\r\n\r\n", "38"], await Run("""
            PRINT block
            PRINT LENGTHS(block)
            TEXTBLOCK block
              first // raw ; _
                second "x"<#CR>

            ENDTEXTBLOCK
            """));
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("TEXTBLOCK t\nx\nENDTEXTBLOCK\nPRINT 1/0"));
        Assert.Equal(4, failure.Line);
    }

    [Theory]
    [InlineData("TEXTBLOCK t\nENDTEXTBLOCK\nPRINT LENGTH(t)", "0")]
    [InlineData("TEXTBLOCK t\nx\nENDTEXTBLOCK\nPRINT t", "x")]
    [InlineData("OPTION SPECIALCHAR\nTEXTBLOCK t\n<#CR>\nENDTEXTBLOCK\nPRINT t", "<#CR>")]
    public async Task TextBlockEmptySingleAndSpecialCharacterPolicy(string script, string expected)
    {
        Assert.Equal([expected], await Run(script));
    }

    [Theory]
    [InlineData("TEXTBLOCK t\nno end")]
    [InlineData("TEXTBLOCK LENGTH\nx\nENDTEXTBLOCK")]
    [InlineData("TEXTBLOCK t\nx\nENDTEXTBLOCK\nCONST t=1")]
    [InlineData("TEXTBLOCK t\nx\nENDTEXTBLOCK\nt=1")]
    public async Task TextBlockRejectsMissingEndNamesDuplicatesAndWrites(string script)
    {
        await Assert.ThrowsAsync<ScriptException>(() => Run(script));
    }

    [Fact]
    public async Task TextBlockCanBeAModuleConstant()
    {
        Assert.Equal(["  value"], await Run("""
            PRINT Sample.text
            MODULE Sample
                TEXTBLOCK text
              value
                ENDTEXTBLOCK
            ENDMODULE
            """));
    }

    [Fact]
    public async Task WithEvaluatesTargetOnceAndRestoresNestedTarget()
    {
        Assert.Equal(["2", "8", "2", "1"], await Run("""
            PUBLIC calls=0
            WITH Pick()
                PRINT .value
                WITH Other
                    PRINT .value
                ENDWITH
                PRINT .value
            ENDWITH
            PRINT calls
            FUNCTION Pick()
                calls=calls+1
                RESULT=Sample
            FEND
            MODULE Sample
                PUBLIC value=2
            ENDMODULE
            MODULE Other
                PUBLIC value=8
            ENDMODULE
            """));
    }

    [Fact]
    public async Task WithRestoresTargetOnExceptBreakAndReturn()
    {
        Assert.Equal(["2", "2", "4"], await Run("""
            WITH Sample
                TRY
                    WITH Other
                        PRINT 1/0
                    ENDWITH
                EXCEPT
                    PRINT .value
                ENDTRY
                WHILE TRUE
                    WITH Other
                        BREAK
                    ENDWITH
                WEND
                PRINT .value
            ENDWITH
            PRINT Read()
            FUNCTION Read()
                WITH Other
                    RESULT=.value
                    EXIT
                ENDWITH
            FEND
            MODULE Sample
                PUBLIC value=2
            ENDMODULE
            MODULE Other
                PUBLIC value=4
            ENDMODULE
            """));
    }

    [Fact]
    public async Task WithSupportsFieldMutationEvalAndByReferenceCalls()
    {
        Assert.Equal(["3", "3"], await Run("""
            WITH Sample
                .value=2
                Increment(.value)
                PRINT EVAL(".value")
            ENDWITH
            PRINT Sample.value
            PROCEDURE Increment(VAR number)
                number=number+1
            FEND
            MODULE Sample
                PUBLIC value=1
            ENDMODULE
            """));
    }

    [Theory]
    [InlineData("PRINT .value")]
    [InlineData("WITH 1\nENDWITH")]
    [InlineData("WITH NOTHING\nENDWITH")]
    [InlineData("WITH EMPTY\nENDWITH")]
    [InlineData("WITH NULL\nENDWITH")]
    [InlineData("WITH object")]
    public async Task WithRejectsMissingContextInvalidTargetAndMissingEnd(string script)
    {
        await Assert.ThrowsAsync<ScriptException>(() => Run(script));
    }

    [Fact]
    public async Task ShortCircuitAppliesOnlyToConditionsOutsideParentheses()
    {
        Assert.Equal(["0", "1", "1", "2", "2", "2", "0"], await Run("""
            OPTION SHORTCIRCUIT
            PUBLIC calls=0
            IF FALSE AND ProbeSideEffect() THEN PRINT "bad"
            PRINT calls
            IF (FALSE AND ProbeSideEffect()) THEN PRINT "bad"
            PRINT calls
            IF TRUE OR ProbeSideEffect() THEN PRINT calls
            DIM x=TRUE OR ProbeSideEffect()
            PRINT calls
            IFB FALSE AND ProbeSideEffect()
                PRINT "bad"
            ENDIF
            PRINT calls
            WHILE FALSE AND ProbeSideEffect()
                PRINT "bad"
            WEND
            REPEAT
            UNTIL TRUE OR ProbeSideEffect()
            PRINT calls
            IF 2 AND 1 THEN PRINT "bad" ELSE PRINT 0
            FUNCTION ProbeSideEffect()
                calls=calls+1
                RESULT=TRUE
            FEND
            """));
    }

    [Fact]
    public async Task DefaultEvaluationRemainsEager()
    {
        Assert.Equal(["2"], await Run("""
            PUBLIC calls=0
            IF FALSE AND ProbeSideEffect() THEN PRINT "bad"
            IF TRUE OR ProbeSideEffect() THEN calls=calls
            PRINT calls
            FUNCTION ProbeSideEffect()
                calls=calls+1
                RESULT=TRUE
            FEND
            """));
    }

    [Theory]
    [InlineData("OPTION SHORTCIRCUIT\nIF TRUE OR 1/0 THEN PRINT 1", "1")]
    [InlineData("OPTION SHORTCIRCUIT\nIF FALSE AND 1/0 THEN PRINT 2\nPRINT 0", "0")]
    public async Task ShortCircuitSkipsErrors(string script, string expected)
    {
        Assert.Equal([expected], await Run(script));
    }

    [Fact]
    public async Task ParenthesizedErrorsAndEvalStayEager()
    {
        await Assert.ThrowsAsync<ScriptException>(() => Run(
            "OPTION SHORTCIRCUIT\nIF (FALSE AND 1/0) THEN PRINT 1"));
        await Assert.ThrowsAsync<ScriptException>(() => Run(
            "OPTION SHORTCIRCUIT\nPRINT EVAL(\"TRUE OR 1/0\")"));
    }

}
