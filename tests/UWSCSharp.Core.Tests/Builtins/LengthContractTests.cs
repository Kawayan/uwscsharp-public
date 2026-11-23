using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class LengthContractTests
{
    [Theory]
    [InlineData("LENGTH(\"\")", 0)]
    [InlineData("LENGTH(\"abc\")", 3)]
    [InlineData("LENGTH(\"日本語\")", 3)]
    [InlineData("LENGTH(\"A😀B\")", 3)]
    [InlineData("LENGTH(\"e\"+CHR($301))", 2)]
    [InlineData("LENGTH(CHR(0))", 1)]
    [InlineData("LENGTH(EMPTY)", 0)]
    [InlineData("LENGTH(TRUE)", 1)]
    [InlineData("LENGTH(FALSE)", 1)]
    [InlineData("LENGTH(12345)", 5)]
    [InlineData("LENGTH(-12.5)", 5)]
    [InlineData("lEnGtH(\"abc\")", 3)]
    public void StringsAndFiniteScalarsUseLogicalCharacters(string expression, int expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Number);
    }

    [Theory]
    [InlineData(0xdc00, 1)]
    [InlineData(0xd800, 1)]
    [InlineData(0xd800dc00, 1)]
    [InlineData(0xdc00dc01, 2)]
    [InlineData(0xd8000041, 2)]
    public void InvalidUtf16UnitsAreCountedAndCopyRemainsConsistent(uint units, int expected)
    {
        string text = units <= 0xffff ? new string((char)units, 1)
            : new string([(char)(units >> 16), (char)units]);
        Assert.Equal(expected, StringOperations.Length(text));
        for (int position = 1; position <= expected; position++)
        {
            Assert.Equal(1, StringOperations.Length(
                StringOperations.Copy(text, position, 1)));
        }

        Assert.Equal("", StringOperations.Copy(text, expected + 1));
        Assert.Equal(3, StringOperations.Position("b", "a\udc00b"));
    }

    [Fact]
    public async Task ArraysCountFirstAxisAndTablesCountKeysWithoutMutation()
    {
        Assert.Equal(["2", "3", "0", "3", "1", "0"], await Run("""
            DIM a[1][2]=1,2,3,4,5,6
            DIM vacant[]
            PRINT LENGTH(a)
            PRINT LENGTH(a[0])
            PRINT LENGTH(vacant)
            PRINT a[0][2]
            HASHTBL h
            h["key"]=a
            PRINT LENGTH(h)
            v=h["key",HASH_REMOVE]
            PRINT LENGTH(h)
            """));
    }

    [Fact]
    public void FullFiniteRangeLargeStringAndArrayLimitsRemainSupported()
    {
        Assert.Equal(23, Length(Value.Numeric(double.MaxValue)));
        Assert.Equal(24, Length(Value.Numeric(double.MinValue)));
        Assert.Equal(2, Length(Value.Numeric(-0d)));
        Assert.Equal(2, BuiltinFunctions.Evaluate("LENGTH",
            [Value.String("a\udc00")], sameString: true).Number);
        string text = new('x', 1_000_001);
        Assert.Equal(text.Length, Length(Value.String(text)));
        var array = UwscArray.Create([999999]);
        Assert.Equal(1_000_000, Length(Value.FromArray(array)));
        Assert.Equal(ValueKind.Empty, array.Get(0).Kind);
    }

    [Theory]
    [InlineData("LENGTH(NULL)")]
    [InlineData("LENGTH(NOTHING)")]
    public async Task InvalidTypesAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void PureApiRejectsObjectErrorsAndNonfiniteNumbers()
    {
        Value[] invalid = [Value.FromObject(new object()), Value.FromError(5),
            new(ValueKind.Null), new(ValueKind.Omitted),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Length(value));
        }
    }

    [Theory]
    [InlineData("LENGTH()")]
    [InlineData("LENGTH(,)")]
    [InlineData("LENGTH(,Tick())")]
    [InlineData("LENGTH(Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=\"abc\"\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["tick", "3"], await Run("PRINT LENGTH(Tick())\n"
            + "FUNCTION Tick()\nPRINT \"tick\"\nRESULT=\"abc\"\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT LENGTH(NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectEnumerationCountsWithoutReadingMembersOrChangingSnapshots(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter(cancel ? cancellation : null);
        List<string> output = [];
        const string source = "o=CREATEOLEOBJ(\"fixture\")\nGETOLEITEM(o)\nPRINT LENGTH(o)\nPRINT ALL_OLE_ITEM[0]";
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new Interpreter(windows: adapter).RunAsync(source,
                    output.Add, cancellation.Token));
            Assert.Empty(output);
        }
        else
        {
            await new Interpreter(windows: adapter).RunAsync(source, output.Add);
            Assert.Equal(["2", "snapshot"], output);
        }

        Assert.Equal(1, adapter.Enumerations);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task EnumerationHostFailurePropagatesAndReleasesSession()
    {
        var adapter = new Adapter(null) { Fail = true };
        List<string> output = [];
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nTRY\nPRINT LENGTH(o)\n"
                    + "EXCEPT\nPRINT \"caught\"\nENDTRY", output.Add));
        Assert.Equal("enumeration failed", error.Message);
        Assert.Empty(output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task CancellationBeforeArgumentsSkipsSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT LENGTH(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static int Length(Value input) =>
        (int)BuiltinFunctions.Evaluate("LENGTH", [input]).Number;

    private sealed class Adapter(CancellationTokenSource? cancel)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Enumerations;
        internal int Disposals;
        internal bool Fail;

        public IReadOnlyList<Value> AllOleItems => [Value.String("snapshot")];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(arguments[0].Kind == ValueKind.Object
                ? Value.Numeric(1) : Value.FromObject(this));

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(
            Value target, CancellationToken token)
        {
            Enumerations++;
            if (Fail)
            {
                throw new InvalidOperationException("enumeration failed");
            }

            cancel?.Cancel();
            return ValueTask.FromResult<IReadOnlyList<Value>>(
                [Value.FromObject(new object()), Value.FromError(5)]);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
