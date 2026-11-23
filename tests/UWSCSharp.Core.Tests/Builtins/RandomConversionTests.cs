using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class RandomConversionTests
{
    private const double Scale = 1.0 / 4294967296.0;

    [Fact]
    public void FixedSeedMatchesGoldenStatesAndRangeScaling()
    {
        uint[] states = [1, 134775814, 3698175007, 870078620,
            1172187917, 2884733762, 1368768587, 694906232,
            1598751577, 1828254910, 352239543, 2039224980];
        int[] million = [0, 31379, 861048, 202580, 272921,
            671654, 318691, 161795, 372238, 425673, 82012, 474794];
        var fractions = new UwscRandom(0);
        var bounded = new UwscRandom(0);
        for (int index = 0; index < states.Length; index++)
        {
            Assert.Equal(states[index] * Scale, fractions.Next());
            Assert.Equal(million[index], bounded.Next(1000000));
        }
    }

    [Fact]
    public void ExtremeSeedsWrapAndNegativeBoundsKeepSignedResults()
    {
        Assert.Equal(1.0 - Scale, new UwscRandom(1298181734).Next());
        Assert.Equal(0, new UwscRandom(649090867).Next());
        var random = new UwscRandom(0);
        Assert.Equal(0, random.Next(-1));
        Assert.Equal(134775813, random.Next(-1));
        Assert.Equal(-596792290, random.Next(-1));
        Assert.Equal(435039310, random.Next(int.MinValue));
    }

    [Fact]
    public async Task ParallelCallsConsumeEveryStateExactlyOnce()
    {
        const int count = 10000;
        var sequential = new UwscRandom(uint.MaxValue);
        var shared = new UwscRandom(uint.MaxValue);
        double[] expected = [.. Enumerable.Range(0, count)
            .Select(_ => sequential.Next()).Order()];
        double[] actual = new double[count];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int index = worker; index < count; index += 8)
            {
                actual[index] = shared.Next();
            }
        })));
        Assert.Equal(expected, actual.Order().ToArray());
        Assert.Equal(sequential.Next(), shared.Next());
    }

    [Theory]
    [InlineData("EMPTY", "0")]
    [InlineData("TRUE", "0")]
    [InlineData("FALSE", "0")]
    [InlineData(".9", "0")]
    [InlineData("-.9", "0")]
    [InlineData("3.9", "0")]
    [InlineData("-3.9", "134775813")]
    [InlineData("2147483647.9", "67387906")]
    [InlineData("-2147483648.9", "67387907")]
    [InlineData("\"1E2\"", "3")]
    [InlineData("\"-1e-999\"", "0")]
    [InlineData("$7FFFFFFF", "67387906")]
    public async Task BoundIsTruncatedAfterFiniteConversion(string value, string second)
    {
        var random = new UwscRandom(0);
        List<string> output = [];
        await new Interpreter(random).RunAsync(
            $"RANDOM(0)\nPRINT RANDOM({value})", output.Add);
        Assert.Equal([second], output);
        Assert.Equal(3698175007 * Scale, random.Next());
    }

    [Fact]
    public async Task LongTextUsesTheCommonGrammar()
    {
        var random = new UwscRandom(0);
        await new Interpreter(random).RunAsync(
            "RANDOM(\"" + new string('0', 100) + "1\")", _ => { });
        Assert.Equal(134775814 * Scale, random.Next());
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("\"$1\"")]
    [InlineData("\"0x10\"")]
    [InlineData("\"1e\"")]
    [InlineData("\"1e+\"")]
    [InlineData("\"1d2\"")]
    [InlineData("\"１.９\"")]
    [InlineData("\"1,234.5\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1e309\"")]
    [InlineData("\"1\"+CHR(0)+\"x\"")]
    [InlineData("\"1\"+CHR(325)+\"2\"")]
    public async Task InvalidBoundDoesNotConsumeState(string value)
    {
        var random = new UwscRandom(0);
        List<string> output = [];
        await new Interpreter(random).RunAsync(
            $"TRY\nRANDOM({value})\nEXCEPT\nPRINT \"caught\"\nENDTRY",
            output.Add);
        Assert.Equal(["caught"], output);
        Assert.Equal(Scale, random.Next());
    }

    [Fact]
    public async Task AdapterKindsAreRejectedWithoutMutatingValuesOrState()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var random = new UwscRandom(0);
            var adapter = new ValueAdapter(value);
            List<string> output = [];
            await new Interpreter(random, windows: adapter).RunAsync(
                "TRY\nRANDOM(CREATEOLEOBJ(\"input\"))\n"
                + "EXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
            Assert.Equal(["caught"], output);
            Assert.Equal(Scale, random.Next());
            Assert.True(adapter.Disposed);
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task RuntimeOmittedValueUsesTheOptionalDefault()
    {
        var random = new UwscRandom(0);
        var adapter = new ValueAdapter(new Value(ValueKind.Omitted));
        List<string> output = [];
        await new Interpreter(random, windows: adapter).RunAsync(
            "PRINT RANDOM(CREATEOLEOBJ(\"input\"))", output.Add);
        Assert.Equal(["2.3283064365386963E-10"], output);
        Assert.Equal(134775814 * Scale, random.Next());
        Assert.True(adapter.Disposed);
    }

    [Theory]
    [InlineData("RANDOM(,)")]
    [InlineData("RANDOM(0,Tick())")]
    [InlineData("RANDOM(Tick(),Tick())")]
    [InlineData("RANDOM(,Tick())")]
    [InlineData("RANDOM(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffectsAndStateChanges(string call)
    {
        var random = new UwscRandom(0);
        List<string> output = [];
        await new Interpreter(random).RunAsync(
            "PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=100\nFEND", output.Add);
        Assert.Equal(["caught", "0"], output);
        Assert.Equal(Scale, random.Next());
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndInjectedStateSurvivesRuns()
    {
        var random = new UwscRandom(0);
        List<string> output = [];
        await new Interpreter(random).RunAsync(
            "PUBLIC calls=0\nPRINT rAnDoM(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1000000\nFEND", output.Add);
        await new Interpreter(random).RunAsync("PRINT RANDOM(1000000)", output.Add);
        Assert.Equal(["0", "1", "31379"], output);
        Assert.Equal(3698175007 * Scale, random.Next());
    }

    [Fact]
    public async Task NoArgumentReturnsFractionsAndErrorsKeepSourceLine()
    {
        var random = new UwscRandom(0);
        List<string> output = [];
        await new Interpreter(random).RunAsync("PRINT RANDOM()", output.Add);
        Assert.Equal(["2.3283064365386963E-10"], output);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(random).RunAsync("PRINT 1\nRANDOM(2147483648)", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Equal(134775814 * Scale, random.Next());
    }

    [Fact]
    public async Task CancellationBeforeNoArgumentCallDoesNotConsumeState()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var random = new UwscRandom(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(random).RunAsync("RANDOM()", _ => { }, cancellation.Token));
        Assert.Equal(Scale, random.Next());
    }

    private sealed class ValueAdapter(Value value) : IWindowsAutomation,
        IWindowsAutomationSession
    {
        public bool Disposed
        {
            get;
            private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult(value);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
