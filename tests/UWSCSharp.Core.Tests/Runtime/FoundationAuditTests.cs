using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FoundationAuditTests
{
    private sealed class SnapshotAutomation : IWindowsAutomation, IWindowsAutomationSession
    {
        public IReadOnlyList<string> AllItems => ["b", "a"];

        internal bool Disposed
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) => ValueTask.FromResult(Value.Numeric(2));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("QSORT(collected, QSRT_A)", "a")]
    [InlineData("SETCLEAR(collected, \"changed\")", "changed")]
    public async Task MutatingCollectedItemsPreservesPublishedSnapshot(string operation, string first)
    {
        var automation = new SnapshotAutomation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "GETITEM(1, ITM_BTN)\ncollected=ALL_ITEM_LIST\n" + operation
            + "\nPRINT collected[0]\nPRINT ALL_ITEM_LIST[0]", output.Add);
        Assert.Equal([first, "b"], output);
        Assert.Equal(["b", "a"], automation.AllItems);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteValuesFromPublicAdapters(double number)
    {
        var value = new Value(ValueKind.Number, number);
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Number(value));
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Integer(value));
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate(
            "external AND 1", new Dictionary<string, Value> { ["external"] = value }));
        Assert.Throws<InvalidOperationException>(() => WindowsFunctions.Validate(
            "GETITEM", [value, Value.Numeric(1)]));
        Assert.Throws<InvalidOperationException>(() => FileFunctions.Validate(
            "FGET", [value, Value.Numeric(1)]));
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Calculate(
            UwscArray.FromValues([value]), 1));
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, -3)]
    [InlineData(3, 7)]
    [InlineData(4, 2)]
    public void SparseNumericArraySupportsEveryAdoptedAggregation(int mode, double expected)
    {
        var values = UwscArray.FromValues(
            [Value.Numeric(-3), new(ValueKind.Empty), Value.Numeric(7), Value.String("2")]);
        Assert.Equal(Value.Numeric(expected), ArrayOperations.Calculate(values, mode));
        Value empty = ArrayOperations.Calculate(values, mode, 99);
        Assert.Equal(mode == 1 ? Value.Numeric(0) : new(ValueKind.Empty), empty);
        Assert.Equal(Value.Numeric(-3), values.Get(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void EmptyArrayStillRejectsInvalidAggregationMode(int mode)
    {
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Calculate(
            UwscArray.FromValues([]), mode));
    }

    [Theory]
    [InlineData("2147483648.9", "2147483648")]
    [InlineData("-2147483649.9", "-2147483649")]
    [InlineData("\"1e2\"", "100")]
    [InlineData("EMPTY", "0")]
    public async Task IntBuiltinDoesNotNarrowItsResultToAnAdapterInteger(string input, string expected)
    {
        Assert.Equal([expected], await Run("PRINT INT(" + input + ")"));
    }

    [Theory]
    [InlineData("2147483647.9", int.MaxValue)]
    [InlineData("-2147483648.9", int.MinValue)]
    [InlineData("$FFFFFFFF", -1)]
    [InlineData("$80000000", int.MinValue)]
    [InlineData("1e+2", 100)]
    public void IntegerConversionTruncatesBeforeCheckingBounds(string text, int expected)
    {
        Assert.Equal(expected, NumericOperations.Integer(Value.String(text)));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void IntegerConversionRejectsInvalidOrOverflowingText(string text)
    {
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Integer(Value.String(text)));
    }

    [Fact]
    public void EmptyAndNullKeepDistinctNumericContracts()
    {
        Assert.Equal(0, NumericOperations.Integer(new(ValueKind.Empty)));
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Integer(new(ValueKind.Null)));
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Integer(new(ValueKind.Omitted)));
    }

    [Theory]
    [InlineData("DIM rows[1][1]=2,1,20,10\nQSORT(rows[0],QSRT_A,rows[1])\nPRINT JOIN(rows,\",\")", "1,2,10,20")]
    [InlineData("DIM a[]=2,1\nQSORT(a,QSRT_D,a)\nPRINT JOIN(a,\",\")", "2,1")]
    [InlineData("DIM a[]=2,1\nDIM b[]=20,10\nQSORT(a,QSRT_A,b)\nPRINT JOIN(b,\",\")", "10,20")]
    public async Task QsortPreservesLinkedAndOverlappingOutputSlots(string script, string expected)
    {
        Assert.Equal([expected], await Run(script));
    }

    [Fact]
    public async Task QsortResolvesOutputOnceAndUpdatesBothArrays()
    {
        Assert.Equal(["1,2", "a,b"], await Run("""
            PUBLIC index = 0
            DIM linked[0][1] = "b", "a"
            a = SPLIT("2,1", ",", FALSE, TRUE)
            TRY
                QSORT(a, QSRT_A, linked[NextIndex()])
            EXCEPT
            ENDTRY
            PRINT JOIN(a, ",")
            PRINT JOIN(linked, ",")
            FUNCTION NextIndex()
                RESULT = index
                index = index + 1
            FEND
            """));
    }
}
