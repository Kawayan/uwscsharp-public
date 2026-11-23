using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class TextBlockWithShortCircuitBoundaryTests
{
    [Theory]
    [InlineData("FALSE AND (ProbeSideEffect())", 0)]
    [InlineData("(FALSE) AND ProbeSideEffect()", 0)]
    [InlineData("TRUE AND (FALSE AND ProbeSideEffect())", 1)]
    [InlineData("(FALSE AND ProbeSideEffect()) OR TRUE", 1)]
    [InlineData("FALSE OR (TRUE OR ProbeSideEffect())", 1)]
    [InlineData("ReturnInput(FALSE AND ProbeSideEffect())", 1)]
    [InlineData("TRUE XOR ProbeSideEffect()", 1)]
    [InlineData("0.5 AND ProbeSideEffect()", 0)]
    [InlineData("0.5 OR ProbeSideEffect()", 1)]
    [InlineData("FALSE AND ProbeSideEffect() OR TRUE", 0)]
    [InlineData("TRUE OR ProbeSideEffect() AND ProbeSideEffect()", 0)]
    public async Task ParenthesesCallsAndOperatorPrecedenceKeepTheirEvaluationBoundary(
        string expression, int expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync($$"""
            OPTION SHORTCIRCUIT
            PUBLIC calls=0
            IF {{expression}} THEN calls=calls
            PRINT calls
            FUNCTION ProbeSideEffect()
                calls=calls+1
                RESULT=TRUE
            FEND
            FUNCTION ReturnInput(value)
                RESULT=value
            FEND
            """, output.Add);
        Assert.Equal([expected.ToString()], output);
    }

    [Fact]
    public async Task WithReferenceTargetSurvivesShadowingAndTokenWrites()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC original=Sample
            WITH Sample
                DIM Sample=Other
                Increment(.value)
                PRINT .value
                PRINT TOKEN(",",.text)
                PRINT .text
            ENDWITH
            PRINT original.value
            PRINT Sample.value
            PROCEDURE Increment(VAR value)
                value=value+1
            FEND
            MODULE Sample
                PUBLIC value=1, text="a,b"
            ENDMODULE
            MODULE Other
                PUBLIC value=99
            ENDMODULE
            """, output.Add);
        Assert.Equal(["2", "a", "b", "2", "99"], output);
    }

    [Fact]
    public async Task WithConstantReferenceFailsBeforeCalleeSideEffects()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC calls=0
            WITH Sample
                TRY
                    Increment(.value)
                EXCEPT
                    PRINT calls
                ENDTRY
            ENDWITH
            PROCEDURE Increment(VAR value)
                calls=calls+1
            FEND
            MODULE Sample
                CONST value=1
            ENDMODULE
            """, output.Add);
        Assert.Equal(["0"], output);
    }

    [Fact]
    public async Task CancellationRestoresTheOuterWithBeforeFinally()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new CancelingHost(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                OPTION OPTFINALLY
                WITH Sample
                    TRY
                        WITH Other
                            SLEEP(1)
                        ENDWITH
                    FINALLY
                        PRINT .value
                    ENDTRY
                ENDWITH
                MODULE Sample
                    PUBLIC value=2
                ENDMODULE
                MODULE Other
                    PUBLIC value=8
                ENDMODULE
                """, host, cancellation.Token));
        Assert.Equal(["2"], host.Output);
    }

    private sealed class CancelingHost(CancellationTokenSource cancellation) : IScriptHost
    {
        internal List<string> Output { get; } = [];

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }
    }
}
