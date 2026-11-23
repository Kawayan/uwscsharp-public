using Xunit;

namespace UWSCSharp.Core.Tests;

// 偽の演算部品で配送と検査順序を確認し、GUI・入力・nativeを使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NumericBuiltinRoutingTests
{
    [Theory]
    [InlineData("SQRT(4)", 0, 4, 0)]
    [InlineData("SIN(1)", 1, 1, 0)]
    [InlineData("COS(1)", 2, 1, 0)]
    [InlineData("TAN(1)", 3, 1, 0)]
    [InlineData("EXP(1)", 4, 1, 0)]
    [InlineData("LN(2)", 5, 2, 0)]
    [InlineData("ARCTAN(1)", 6, 1, 0)]
    [InlineData("INT(-1.75)", 7, -1.75, 0)]
    [InlineData("ABS(-2)", 8, -2, 0)]
    [InlineData("LOGN(2,8)", 9, 2, 8)]
    [InlineData("ARCSIN(-0.5)", 10, -0.5, 0)]
    [InlineData("ARCCOS(0.5)", 11, 0.5, 0)]
    [InlineData("CEIL(2147483647.1)", 12, 2147483647.1, 0)]
    [InlineData("ZCUT(-2.5)", 13, -2.5, 0)]
    [InlineData("POWER(-2,3)", 14, -2, 3)]
    public async Task SelectedBuiltinsUseTheCurrentSubjectArithmetic(
        string expression, int operation, double input, double right)
    {
        var arithmetic = new Arithmetic();
        var output = new List<string>();
        await Run("PRINT " + expression, arithmetic, output);
        Assert.Equal(["42"], output);
        Assert.Equal([((NumericBuiltinOperation)operation, input, right)], arithmetic.Calls);
        Assert.Equal(0, arithmetic.Disposals);
    }

    [Theory]
    [InlineData("SQRT(-1)")]
    [InlineData("LN(0)")]
    [InlineData("SIN(NULL)")]
    [InlineData("EXP(\"bad\")")]
    [InlineData("LOGN(1,8)")]
    [InlineData("LOGN(2,0)")]
    [InlineData("ARCSIN(1.01)")]
    [InlineData("ARCCOS(-1.01)")]
    [InlineData("POWER(-2,0.5)")]
    [InlineData("POWER(0,-1)")]
    public async Task ExistingInputAndDomainChecksPrecedeTheBackend(string expression)
    {
        var arithmetic = new Arithmetic();
        var output = new List<string>();
        await Run("TRY\nPRINT " + expression + "\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY",
            arithmetic, output);
        Assert.Equal(["2"], output);
        Assert.Empty(arithmetic.Calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task BackendResultsStillRequireFiniteValues(double result)
    {
        var arithmetic = new Arithmetic { Result = result };
        var output = new List<string>();
        await Run("TRY\nPRINT SQRT(4)\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY",
            arithmetic, output);
        Assert.Equal(["2"], output);
        Assert.Single(arithmetic.Calls);
    }

    [Fact]
    public async Task ArgumentCountIsRejectedBeforeSideEffects()
    {
        var arithmetic = new Arithmetic();
        var output = new List<string>();
        await Run("""
            PUBLIC count=0
            TRY
                PRINT SQRT(MakeOperand(),MakeOperand())
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            PRINT count
            FUNCTION MakeOperand()
                count=count+1
                RESULT=4
            FEND
            """, arithmetic, output);
        Assert.Equal(["3", "0"], output);
        Assert.Empty(arithmetic.Calls);
    }

    [Fact]
    public async Task EvalUsesTheBackendWhilePureApisAndOtherRunsRemainIndependent()
    {
        var arithmetic = new Arithmetic();
        var output = new List<string>();
        await Run("PRINT EVAL(\"SQRT(4)\")\nPRINT LENGTH(\"ab\")",
            arithmetic, output);
        await new Interpreter().RunAsync("PRINT SQRT(4)", output.Add);
        Assert.Equal(["42", "2", "2"], output);
        Assert.Single(arithmetic.Calls);
        Assert.Equal(2, ExpressionEvaluator.Evaluate("SQRT(4)").AsNumber());
        Assert.Equal(2, CompiledNumericExpression.Compile("SQRT(x)", ["x"])
            .Evaluate(new Dictionary<string, double> { ["x"] = 4 }));
    }

    [Fact]
    public async Task RoundKeepsItsManagedMidpointRuleWithInternalArithmetic()
    {
        var arithmetic = new Arithmetic();
        var output = new List<string>();
        await Run("PRINT ROUND(2.675,-2)\nPRINT ROUND(2.685,-2)",
            arithmetic, output);

        Assert.Equal(["2.68", "2.68"], output);
        Assert.Empty(arithmetic.Calls);
    }

    [Fact]
    public async Task BackendFailureRetainsTheCallLineAndCause()
    {
        var expected = new InvalidOperationException("数値関数の失敗");
        var arithmetic = new Arithmetic { Failure = expected };
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT SQRT(4)", arithmetic, []));
        Assert.Equal(2, failure.Line);
        Assert.Same(expected, failure.InnerException);
        Assert.Equal(0, arithmetic.Disposals);
    }

    private static Task Run(string source, Arithmetic arithmetic, List<string> output) =>
        new Interpreter().RunAsync(source, output.Add, default,
            new ScriptRunOptions([]) { NumericArithmetic = arithmetic });

    private sealed class Arithmetic : INumericBuiltinArithmetic, IDisposable
    {
        internal List<(NumericBuiltinOperation Operation, double Left, double Right)> Calls { get; } = [];
        internal double Result = 42;
        internal Exception? Failure;
        internal int Disposals;

        public double EvaluateBuiltin(NumericBuiltinOperation operation, double left, double right)
        {
            Calls.Add((operation, left, right));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }

        public double Evaluate(NumericBinaryOperation operation, double left, double right) => left + right;

        public void Dispose() => Disposals++;
    }
}
