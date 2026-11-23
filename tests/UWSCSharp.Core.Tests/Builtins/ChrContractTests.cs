using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ChrContractTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(32, 32)]
    [InlineData(65, 65)]
    [InlineData(12354, 12354)]
    [InlineData(0xd7ff, 0xd7ff)]
    [InlineData(0xd800, 0xd800)]
    [InlineData(0xdbff, 0xdbff)]
    [InlineData(0xdc00, 0xdc00)]
    [InlineData(0xdfff, 0xdfff)]
    [InlineData(0xe000, 0xe000)]
    [InlineData(0xffff, 0xffff)]
    [InlineData(65.9, 65)]
    [InlineData(-0.5, 0)]
    [InlineData(-double.Epsilon, 0)]
    public void BmpCodesPreserveTheirExactUtf16Unit(double input, int expected)
    {
        string text = Character(Value.Numeric(input));
        Assert.Equal(1, text.Length);
        Assert.Equal(expected, (int)text[0]);
    }

    [Theory]
    [InlineData(0x10000, 0xd800, 0xdc00)]
    [InlineData(0x1f600, 0xd83d, 0xde00)]
    [InlineData(0x10ffff, 0xdbff, 0xdfff)]
    [InlineData(1114111.9, 0xdbff, 0xdfff)]
    public void SupplementaryCodesReturnTheExpectedPair(double input, int high, int low)
    {
        string text = Character(Value.Numeric(input));
        Assert.Equal(2, text.Length);
        Assert.Equal(high, (int)text[0]);
        Assert.Equal(low, (int)text[1]);
    }

    [Theory]
    [InlineData("CHR(\"65\")", 65)]
    [InlineData("CHR(\" 65 \")", 65)]
    [InlineData("CHR(\"6.5e1\")", 65)]
    [InlineData("CHR(TRUE)", 1)]
    [InlineData("CHR(FALSE)", 0)]
    [InlineData("CHR(EMPTY)", 0)]
    [InlineData("cHr(65)", 65)]
    public void FiniteTextAndScalarDefaultsUseTheRegisteredFunction(string expression, int expected)
    {
        string text = ExpressionEvaluator.Evaluate(expression).Text;
        Assert.Equal(1, text.Length);
        Assert.Equal(expected, (int)text[0]);
    }

    [Fact]
    public void LongNumericTextAndHexNumericLiteralsRemainDistinct()
    {
        string zeros = new('0', 10000);
        Assert.Equal("A", Character(Value.String(zeros + "65")));
        Assert.Equal("😀", ExpressionEvaluator.Evaluate("CHR($1F600)").Text);
        Assert.Equal("😀", Character(Value.String("128512")));
        Assert.Throws<InvalidOperationException>(() => Character(Value.String("$1F600")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1.9)]
    [InlineData(0x110000)]
    [InlineData(2147483647)]
    [InlineData(2147483648d)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void TruncatedValuesOutsideTheDeclaredRangeAreRejected(double input)
    {
        Assert.Throws<InvalidOperationException>(() => Character(Value.Numeric(input)));
    }

    [Theory]
    [InlineData("CHR(\"$41\")")]
    [InlineData("CHR(\"65e+\")")]
    [InlineData("CHR(\"６５\")")]
    [InlineData("CHR(\"1,0\")")]
    [InlineData("CHR(\"65\"+CHR(0))")]
    [InlineData("CHR(\"\")")]
    [InlineData("CHR(\" \")")]
    [InlineData("CHR(\"NaN\")")]
    [InlineData("CHR(NULL)")]
    [InlineData("CHR(NOTHING)")]
    public async Task InvalidArgumentsAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void InvalidKindsAndNonfiniteValuesDoNotMutateInputs()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Character(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("CHR()")]
    [InlineData("CHR(,)")]
    [InlineData("CHR(Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=65\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["tick", "A"], await Run("PRINT CHR(Tick())\n"
            + "FUNCTION Tick()\nPRINT \"tick\"\nRESULT=65\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT CHR($110000)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT CHR(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=65\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Character(Value input) =>
        BuiltinFunctions.Evaluate("CHR", [input]).Text;
}
