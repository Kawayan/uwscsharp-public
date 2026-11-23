using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 借用した偽の演算部品で配送を確認し、nativeやGUIを使いません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NumericArithmeticRoutingTests
{
    [Theory]
    [InlineData("7+2", 0)]
    [InlineData("7-2", 1)]
    [InlineData("7*2", 2)]
    [InlineData("7/2", 3)]
    public async Task FourArithmeticOperatorsUseTheBorrowedBackend(
        string expression, int operation)
    {
        var backend = new Arithmetic((_, _, _) => 42);
        var output = new List<string>();
        await Run("PRINT " + expression, backend, output);
        Assert.Equal(["42"], output);
        Assert.Equal([((NumericBinaryOperation)operation, 7d, 2d)], backend.Calls);
        Assert.Equal(0, backend.Disposals);
    }

    [Theory]
    [InlineData("\"x\"+2", "x2")]
    [InlineData("2<3", "1")]
    [InlineData("7 MOD 3", "1")]
    [InlineData("6 AND 3", "2")]
    public async Task OtherOperatorsKeepTheirExistingSemantics(string expression, string expected)
    {
        var backend = new Arithmetic((_, _, _) => throw new InvalidOperationException());
        var output = new List<string>();
        await Run("PRINT " + expression, backend, output);
        Assert.Equal([expected], output);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("NULL+1")]
    public async Task ExistingInputChecksRunBeforeTheBackend(string expression)
    {
        var backend = new Arithmetic((_, _, _) => 42);
        var output = new List<string>();
        await Run("TRY\nPRINT " + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY", backend, output);
        Assert.Equal(["caught"], output);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task BackendResultsStillRequireFiniteValues(double result)
    {
        var backend = new Arithmetic((_, _, _) => result);
        var output = new List<string>();
        await Run("TRY\nPRINT 7+2\nEXCEPT\nPRINT \"caught\"\nENDTRY", backend, output);
        Assert.Equal(["caught"], output);
        Assert.Single(backend.Calls);
    }

    [Fact]
    public async Task ConditionArithmeticAndForUpdatesUseTheSameBackend()
    {
        var backend = new Arithmetic((_, left, right) => left + right + 1);
        var output = new List<string>();
        await Run("IFB 1+1=3\nPRINT \"condition\"\nENDIF\nFOR i=1 TO 3\nPRINT i\nNEXT", backend, output);
        Assert.Equal(["condition", "1", "3"], output);
        Assert.Equal(3, backend.Calls.Count);
    }

    [Fact]
    public async Task BackendFailureKeepsTheExpressionLineAndBorrowedOwnership()
    {
        var backend = new Arithmetic((_, _, _) => throw new InvalidOperationException("backend failure"));
        var output = new List<string>();
        await Run("TRY\nPRINT 7+2\nEXCEPT\nPRINT TRY_ERRLINE\nENDTRY", backend, output);
        Assert.Equal(["2"], output);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public async Task AnotherRunAndPureEvaluationDoNotInheritTheBackend()
    {
        var backend = new Arithmetic((_, _, _) => 42);
        var interpreter = new Interpreter();
        var output = new List<string>();
        await interpreter.RunAsync("PRINT 7+2", output.Add, default,
            new ScriptRunOptions([]) { NumericArithmetic = backend });
        await interpreter.RunAsync("PRINT 7+2", output.Add);
        Assert.Equal(["42", "9"], output);
        Assert.Equal(9, ExpressionEvaluator.Evaluate("7+2").AsNumber());
        Assert.Equal(9, CompiledNumericExpression.Compile("7+2", []).Evaluate(
            new Dictionary<string, double>()));
        Assert.Single(backend.Calls);
    }

    private static Task Run(string source, INumericArithmetic backend, List<string> output) =>
        new Interpreter().RunAsync(source, output.Add, default,
            new ScriptRunOptions([]) { NumericArithmetic = backend });

    private sealed class Arithmetic(Func<NumericBinaryOperation, double, double, double> calculate)
        : INumericArithmetic, IDisposable
    {
        internal List<(NumericBinaryOperation Operation, double Left, double Right)> Calls { get; } = [];

        internal int Disposals;

        public double Evaluate(NumericBinaryOperation operation, double left, double right)
        {
            Calls.Add((operation, left, right));
            return calculate(operation, left, right);
        }

        public void Dispose() => Disposals++;
    }
}
