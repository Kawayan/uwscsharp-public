using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class AscContractTests
{
    [Theory]
    [InlineData("ASC(\"\")", 0)]
    [InlineData("ASC(\"A\")", 65)]
    [InlineData("ASC(\"ABC\")", 65)]
    [InlineData("ASC(\"あいう\")", 12354)]
    [InlineData("ASC(\"😀x\")", 128512)]
    [InlineData("ASC(\"e\"+CHR($301))", 101)]
    [InlineData("ASC(CHR($301)+\"e\")", 769)]
    [InlineData("ASC(CHR(0)+\"A\")", 0)]
    [InlineData("ASC(EMPTY)", 0)]
    [InlineData("ASC(TRUE)", 49)]
    [InlineData("ASC(FALSE)", 48)]
    [InlineData("ASC(65)", 54)]
    [InlineData("ASC(-12.5)", 45)]
    [InlineData("ASC(\"$41\")", 36)]
    [InlineData("aSc(\"A\")", 65)]
    public void FirstCharacterAndTextConversionHaveExplicitSemantics(string expression, int expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Number);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xd7ff)]
    [InlineData(0xd800)]
    [InlineData(0xdbff)]
    [InlineData(0xdc00)]
    [InlineData(0xdfff)]
    [InlineData(0xe000)]
    [InlineData(0xffff)]
    public void IsolatedAndBmpUnitsReturnTheirExactValue(int unit)
    {
        string text = new((char)unit, 1);
        Assert.Equal(unit, Code(Value.String(text)));
        Assert.Equal(unit, Code(Value.String(text + "A")));
    }

    [Theory]
    [InlineData(0xd800, 0xdc00, 0x10000)]
    [InlineData(0xd83d, 0xde00, 0x1f600)]
    [InlineData(0xdbff, 0xdfff, 0x10ffff)]
    public void LeadingPairsReturnTheExpectedCodePoint(int high, int low, int expected)
    {
        string text = new([(char)high, (char)low]);
        Assert.Equal(expected, Code(Value.String(text)));
        Assert.Equal(expected, Code(Value.String(text + "tail")));
    }

    [Fact]
    public void InvalidTrailingUnitsDoNotChangeTheFirstCharacter()
    {
        Assert.Equal(65, Code(Value.String("A\udc00\ud800")));
        Assert.Equal(0xd800, Code(Value.String("\ud800\ud800\udc00")));
        Assert.Equal(0xdc00, Code(Value.String("\udc00\ud800\udc00")));
        Assert.Equal(0, Code(Value.String("\0\ud800\udc00")));
    }

    [Fact]
    public void FiniteLimitsSignedZeroAndLongInputAreSupported()
    {
        Assert.Equal(49, Code(Value.Numeric(double.MaxValue)));
        Assert.Equal(45, Code(Value.Numeric(double.MinValue)));
        Assert.Equal(45, Code(Value.Numeric(-0d)));
        Assert.Equal(48, Code(Value.Numeric(0)));
        string text = "A" + new string('x', 1_000_001);
        Assert.Equal(65, Code(Value.String(text)));
    }

    [Theory]
    [InlineData("", "97")]
    [InlineData("OPTION SAMESTR\n", "97")]
    public async Task SameStringDoesNotChangeTheReturnedCode(string option, string expected)
    {
        Assert.Equal([expected], await Run(option + "PRINT ASC(\"aA\")"));
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
            Assert.Throws<InvalidOperationException>(() => Code(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("ASC(NULL)")]
    [InlineData("ASC(NOTHING)")]
    public async Task InvalidTypesAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Theory]
    [InlineData("ASC()")]
    [InlineData("ASC(,)")]
    [InlineData("ASC(Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=\"A\"\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["tick", "65"], await Run("PRINT ASC(Tick())\n"
            + "FUNCTION Tick()\nPRINT \"tick\"\nRESULT=\"A\"\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT ASC(NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT ASC(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"A\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static int Code(Value input) =>
        (int)BuiltinFunctions.Evaluate("ASC", [input]).Number;
}
