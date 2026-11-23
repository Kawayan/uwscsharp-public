using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class LogNConversionTests
{
    [Theory]
    [InlineData(2, 8, 3)]
    [InlineData(10, 100, 2)]
    [InlineData(4, 2, .5)]
    [InlineData(.5, 8, -3)]
    [InlineData(.5, .125, 3)]
    [InlineData(.25, 2, -.5)]
    [InlineData(double.Epsilon, double.Epsilon, 1)]
    [InlineData(double.MaxValue, double.MaxValue, 1)]
    public void SupportsPositiveBasesAboveAndBelowOne(double basis, double argument, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(basis), Value.Numeric(argument)).Number);
    }

    [Fact]
    public void BasesTwoAndOneHalfCoverEveryFiniteBinaryExponent()
    {
        for (int exponent = -1074; exponent <= 1023; exponent++)
        {
            var argument = Value.Numeric(Math.ScaleB(1, exponent));
            double result = Evaluate(Value.Numeric(2), argument).Number;
            double reverse = Evaluate(Value.Numeric(.5), argument).Number;
            Assert.InRange(result - exponent, -4e-13, 4e-13);
            Assert.Equal(-result, reverse);
        }
    }

    [Theory]
    [InlineData(2, 0L)]
    [InlineData(.5, long.MinValue)]
    public void UnitArgumentHasTheDotNetSignOfZero(double basis, long expected)
    {
        Assert.Equal(expected, BitConverter.DoubleToInt64Bits(
            Evaluate(Value.Numeric(basis), Value.Numeric(1)).Number));
    }

    [Fact]
    public void BasesImmediatelyAroundOneRemainDistinctAndValid()
    {
        var before = Value.Numeric(Math.BitDecrement(1));
        var after = Value.Numeric(Math.BitIncrement(1));
        Assert.Equal(1, Evaluate(before, before).Number);
        Assert.Equal(1, Evaluate(after, after).Number);
        Assert.True(Evaluate(before, Value.Numeric(2)).Number < 0);
        Assert.True(Evaluate(after, Value.Numeric(2)).Number > 0);
        Assert.Equal(-.5000000000000001, Evaluate(after, before).Number);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.MinValue)]
    public void RejectsInvalidBasesEvenWhenTheArgumentIsOne(double basis)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(basis), Value.Numeric(1)));
        Assert.Contains("底", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.MinValue)]
    public void RejectsNonpositiveArguments(double argument)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(2), Value.Numeric(argument)));
        Assert.Contains("真数", error.Message);
    }

    [Fact]
    public void EmptyAndNegativeZeroAreOutsideBothDomains()
    {
        var zero = Value.Numeric(BitConverter.Int64BitsToDouble(long.MinValue));
        Assert.Throws<InvalidOperationException>(() => Evaluate(default, Value.Numeric(1)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(2), default));
        Assert.Throws<InvalidOperationException>(() => Evaluate(zero, Value.Numeric(1)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(2), zero));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteInputsEvenForOtherwiseConstantResults(double input)
    {
        Value invalid = new(ValueKind.Number, input);
        Assert.Throws<InvalidOperationException>(() => Evaluate(invalid, Value.Numeric(1)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(2), invalid));
    }

    [Fact]
    public void RejectsUnsupportedKindsAtBothArgumentsWithoutMutation()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value, Value.Numeric(8)));
            Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(2), value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("2", "8", 3)]
    [InlineData("1e2", "1E4", 2)]
    [InlineData("\t .25 \r\n", "2", -.5)]
    [InlineData("00000000000000000000000000000000000000002", "8", 3)]
    [InlineData(".5", ".125", 3)]
    [InlineData("1e-300", "1e300", -1)]
    public void ConvertsBothArgumentsUsingTheInvariantGrammar(
        string basis, string argument, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(basis), Value.String(argument)).Number);
    }

    [Theory]
    [InlineData("$4")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("1e+")]
    [InlineData("1e-999")]
    [InlineData("0x10")]
    [InlineData("1e")]
    [InlineData("1d2")]
    [InlineData("１.９")]
    [InlineData("1,234.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1\0x")]
    [InlineData("1\u0145+2")]
    public void RejectsInvalidTextAtBothArguments(string text)
    {
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.String(text), Value.Numeric(8)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(2), Value.String(text)));
    }

    [Theory]
    [InlineData("LOGN()")]
    [InlineData("LOGN(Tick())")]
    [InlineData("LOGN(,Tick())")]
    [InlineData("LOGN(Tick(),)")]
    [InlineData("LOGN(Tick(),Tick(),Tick())")]
    [InlineData("LOGN(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=calls\nFEND"));
    }

    [Fact]
    public async Task EvaluatesBothArgumentsOnceBeforeConversionAndDomainChecks()
    {
        Assert.Equal(["1", "2", "2", "2"], await Run(
            "PUBLIC calls=0\nPRINT lOgN(Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=2*calls\nFEND"));
        foreach (string expression in new[] { "LOGN(\"bad\",Tick())",
            "LOGN(0,Tick())", "LOGN(Tick(),\"bad\")" })
        {
            Assert.Equal(["caught", "1"], await Run(
                "PUBLIC calls=0\nTRY\nPRINT " + expression
                + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
                + "FUNCTION Tick()\ncalls=calls+1\nRESULT=2\nFEND"));
        }
    }

    [Fact]
    public void ConvertsBaseThenArgumentBeforeCheckingDomains()
    {
        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.String("bad"), new Value(ValueKind.Number, double.NaN)));
        Assert.Contains("数値に変換", first.Message);
        InvalidOperationException second = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(0), Value.String("bad")));
        Assert.Contains("数値に変換", second.Message);
        InvalidOperationException domain = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(0), Value.Numeric(0)));
        Assert.Contains("底", domain.Message);
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeVariableResolution()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("LOGN(missing)"));
        Assert.Contains("LOGN", error.Message);
    }

    [Theory]
    [InlineData("LOGN(0,1)")]
    [InlineData("LOGN(1,2)")]
    [InlineData("LOGN(2,0)")]
    [InlineData("LOGN(2,\"$2\")")]
    public async Task DomainErrorsKeepSourceLineAndSupportExcept(string expression)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT " + expression));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT " + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateArguments()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT LOGN(Tick(),Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value basis, Value argument) =>
        ExpressionEvaluator.Evaluate("LOGN(basis,argument)",
            new Dictionary<string, Value> { ["basis"] = basis, ["argument"] = argument });
}
