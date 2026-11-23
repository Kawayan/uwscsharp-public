using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class SleepConversionTests
{
    [Fact]
    public async Task ZeroSecondsYieldsBeforeContinuingTheScript()
    {
        var context = new QueuedContext();
        var host = new Host();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task run;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            run = new Interpreter().RunAsync("SLEEP(0)\nPRINT 1", host);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.False(run.IsCompleted);
        Assert.Empty(host.Output);
        Assert.Single(context.Pending);
        context.Drain();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([TimeSpan.Zero], host.Waits);
        Assert.Equal(["1"], host.Output);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-0", 0)]
    [InlineData("EMPTY", 0)]
    [InlineData("FALSE", 0)]
    [InlineData("TRUE", 10000000)]
    [InlineData("0.125", 1250000)]
    [InlineData("1e-7", 1)]
    [InlineData("1e-300", 0)]
    [InlineData("\"1e-3\"", 10000)]
    [InlineData("\"-1e-999\"", 0)]
    [InlineData("2147483.647", 21474836470000)]
    public async Task AcceptedValuesReachTheHostOnce(string value, long ticks)
    {
        var host = new Host();
        await new Interpreter().RunAsync($"SLEEP({value})\nPRINT 1", host);
        Assert.Equal([TimeSpan.FromTicks(ticks)], host.Waits);
        Assert.Equal(["1"], host.Output);
    }

    [Fact]
    public async Task LongTextUsesTheCommonFiniteGrammar()
    {
        var host = new Host();
        await new Interpreter().RunAsync("SLEEP(\""
            + new string('0', 100) + "0.001\")", host);
        Assert.Equal([TimeSpan.FromMilliseconds(1)], host.Waits);
    }

    [Theory]
    [InlineData("SLEEP()")]
    [InlineData("SLEEP(,)")]
    [InlineData("SLEEP(,Tick())")]
    [InlineData("SLEEP(0,Tick())")]
    [InlineData("SLEEP(Tick(),Tick(),Tick())")]
    public async Task InvalidArityHasNoArgumentOrHostEffects(string call)
    {
        var host = new Host();
        await new Interpreter().RunAsync("PUBLIC calls=0\nTRY\n"
            + call + "\nEXCEPT\nPRINT calls\nENDTRY\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=0\nFEND", host);
        Assert.Equal(["0"], host.Output);
        Assert.Empty(host.Waits);
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("2147483.648")]
    [InlineData("1e+308")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"$0\"")]
    [InlineData("\"0x0\"")]
    [InlineData("\"0e\"")]
    [InlineData("\"0e+\"")]
    [InlineData("\"0d2\"")]
    [InlineData("\"０.００１\"")]
    [InlineData("\"0,000\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1e309\"")]
    [InlineData("\"0\"+CHR(0)")]
    public async Task InvalidValuesAreCatchableAndNeverReachTheHost(string value)
    {
        var host = new Host();
        await new Interpreter().RunAsync($"TRY\nSLEEP({value})\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY", host);
        Assert.Equal(["caught"], host.Output);
        Assert.Empty(host.Waits);
    }

    [Fact]
    public async Task NonfiniteAndUnsupportedAdapterValuesAreRejected()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity),
            Value.Numeric(Math.BitIncrement(int.MaxValue / 1000d)),
            Value.Numeric(-double.Epsilon)];
        foreach (Value value in invalid)
        {
            var adapter = new ValueAdapter(value);
            var host = new Host();
            await new Interpreter(windows: adapter).RunAsync("""
                TRY
                    SLEEP(CREATEOLEOBJ("input"))
                EXCEPT
                    PRINT "caught"
                ENDTRY
                """, host);
            Assert.Equal(["caught"], host.Output);
            Assert.Empty(host.Waits);
            Assert.True(adapter.Disposed);
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task ArgumentRunsOnceNamesIgnoreCaseAndReturnValueIsEmpty()
    {
        var host = new Host();
        await new Interpreter().RunAsync("""
            PUBLIC calls=0
            PRINT sLeEp(Tick())
            PRINT calls
            FUNCTION Tick()
                calls=calls+1
                RESULT=0
            FEND
            """, host);
        Assert.Equal(["", "1"], host.Output);
        Assert.Equal([TimeSpan.Zero], host.Waits);
    }

    [Fact]
    public async Task InvalidValueHasItsSourceLine()
    {
        var host = new Host();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("PRINT 1\nSLEEP(-1)", host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Waits);
    }

    [Fact]
    public async Task CancellationDuringZeroYieldPreventsFollowingEffects()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new QueuedContext();
        var host = new Host();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task run;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            run = new Interpreter().RunAsync("SLEEP(0)\nPRINT 1", host, cancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        cancellation.Cancel();
        context.Drain();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(host.Output);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        internal List<(SendOrPostCallback Callback, object? State)> Pending { get; } = [];

        public override void Post(SendOrPostCallback callback, object? state) =>
            Pending.Add((callback, state));

        internal void Drain()
        {
            foreach ((SendOrPostCallback callback, object? state) in Pending.ToArray())
            {
                callback(state);
            }

            Pending.Clear();
        }
    }

    private sealed class Host : IScriptHost
    {
        internal List<TimeSpan> Waits { get; } = [];

        internal List<string> Output { get; } = [];

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Waits.Add(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class ValueAdapter(Value value) : IWindowsAutomation,
        IWindowsAutomationSession
    {
        internal bool Disposed
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
