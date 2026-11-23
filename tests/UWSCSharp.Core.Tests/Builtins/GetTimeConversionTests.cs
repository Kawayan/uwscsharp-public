using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class GetTimeConversionTests
{
    private static readonly string[] Names = ["YY", "MM", "DD", "HH", "NN", "SS",
        "ZZ", "WW", "YY2", "MM2", "DD2", "HH2", "NN2", "SS2", "ZZ2", "YY4"];

    public static IEnumerable<object[]> VariableNames =>
        Names.Select(name => new object[] { "G_TIME_" + name });

    private static string Snapshot => string.Join("\n",
        Names.Select(name => "PRINT G_TIME_" + name));

    [Fact]
    public async Task CurrentTimePublishesEveryFieldAndExcludesFractionalSeconds()
    {
        DateTimeOffset instant = new(2000, 2, 29, 12, 34, 56, 789, TimeSpan.Zero);
        var clock = new CountingClock(instant.AddTicks(1234));
        List<string> output = [];
        await new Interpreter(timeProvider: clock).RunAsync(
            "PRINT GETTIME()\n" + Snapshot, output.Add);
        Assert.Equal(["5142896", "2000", "2", "29", "12", "34", "56", "789", "2",
            "00", "02", "29", "12", "34", "56", "789", "2000"], output);
        Assert.Equal(1, clock.Calls);
    }

    [Theory]
    [InlineData("00010101", "-63082281600")]
    [InlineData("18991229120000", "-3155889600")]
    [InlineData("19000228", "-3150662400")]
    [InlineData("20000229", "5097600")]
    [InlineData("99991231235959", "252455615999")]
    [InlineData("2000/01/01 00:00:01", "1")]
    public async Task ExplicitDateHasNoClockDependency(string basis, string seconds)
    {
        var clock = new CountingClock(default, fail: true);
        List<string> output = [];
        await new Interpreter(timeProvider: clock).RunAsync(
            $"PRINT GETTIME(0,\"{basis}\")", output.Add);
        Assert.Equal([seconds], output);
        Assert.Equal(0, clock.Calls);
    }

    [Theory]
    [InlineData("GETTIME()")]
    [InlineData("GETTIME(,)")]
    [InlineData("GETTIME(0,\"\")")]
    [InlineData("GETTIME(0,EMPTY)")]
    public async Task EmptyAndOmittedBaseUseTheClock(string call)
    {
        var clock = new CountingClock(new DateTimeOffset(2000, 1, 1, 0, 0, 1, TimeSpan.Zero));
        List<string> output = [];
        await new Interpreter(timeProvider: clock).RunAsync("PRINT " + call, output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(1, clock.Calls);
    }

    [Theory]
    [InlineData("1.5", "129600")]
    [InlineData("-0.5", "-43200")]
    [InlineData("\"1E2\"", "8640000")]
    [InlineData("EMPTY", "0")]
    [InlineData("TRUE", "86400")]
    [InlineData("FALSE", "0")]
    [InlineData("\"1e-999\"", "0")]
    [InlineData("\"-1e-999\"", "0")]
    public async Task FiniteDayConversionUsesTheCommonContract(string days, string seconds)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(
            $"PRINT GETTIME({days},\"20000101\")", output.Add);
        Assert.Equal([seconds], output);
    }

    [Fact]
    public async Task LongTextAndNumericBaseAreAccepted()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT GETTIME(\""
            + new string('0', 100) + "1\",20000101)", output.Add);
        Assert.Equal(["86400"], output);
    }

    [Theory]
    [InlineData("5e-9", "0", "0")]
    [InlineData("-5e-9", "-1", "999")]
    [InlineData("1e-3", "86", "400")]
    [InlineData("-1e-3", "-87", "600")]
    public async Task FractionalDaysExposeMillisecondsButReturnWholeCalendarSeconds(
        string days, string seconds, string milliseconds)
    {
        List<string> output = [];
        await new Interpreter().RunAsync($"PRINT GETTIME(\"{days}\",\"20000101\")\n"
            + "PRINT G_TIME_ZZ", output.Add);
        Assert.Equal([seconds, milliseconds], output);
    }

    [Theory]
    [InlineData("\"$1\"")]
    [InlineData("\"1e+\"")]
    [InlineData("\"1d2\"")]
    [InlineData("\"１.９\"")]
    [InlineData("\"1,234.5\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1e309\"")]
    [InlineData("\"1\"+CHR(0)")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("1e+308")]
    public async Task InvalidDaysKeepTheWholeSnapshotAndDoNotReadTheClock(string days)
    {
        var clock = new CountingClock(default, fail: true);
        List<string> output = [];
        string script = "GETTIME(0,\"20000229\")\n" + Snapshot
            + $"\nTRY\nGETTIME({days},\"20000101\")\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + Snapshot;
        await new Interpreter(timeProvider: clock).RunAsync(script, output.Add);
        Assert.Equal("caught", output[16]);
        Assert.Equal(output.Take(16), output.Skip(17));
        Assert.Equal(0, clock.Calls);
    }

    [Theory]
    [InlineData("20000230")]
    [InlineData("19000229")]
    [InlineData("00000101")]
    [InlineData("20000101000060")]
    [InlineData("200001010000001")]
    [InlineData("200001010000")]
    [InlineData("2000\t0101")]
    [InlineData("２００００１０１")]
    [InlineData("20000101\0")]
    public async Task InvalidBaseKeepsSnapshotWithoutReadingClock(string basis)
    {
        var clock = new CountingClock(default, fail: true);
        List<string> output = [];
        await new Interpreter(timeProvider: clock).RunAsync(
            "GETTIME(0,\"20000101\")\nTRY\nGETTIME(0,\"" + basis
            + "\")\nEXCEPT\nPRINT G_TIME_YY4\nENDTRY", output.Add);
        Assert.Equal(["2000"], output);
        Assert.Equal(0, clock.Calls);
    }

    [Theory]
    [InlineData("-1e-9", "00010101")]
    [InlineData("1", "99991231")]
    public async Task RangeFailureIsCatchableAndKeepsSnapshot(string days, string basis)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("GETTIME(0,\"20000101\")\nTRY\n"
            + $"GETTIME({days},\"{basis}\")\nEXCEPT\nPRINT G_TIME_YY4\nENDTRY", output.Add);
        Assert.Equal(["2000"], output);
    }

    [Theory]
    [MemberData(nameof(VariableNames))]
    public async Task ConstantTargetPreventsEverySnapshotWrite(string target)
    {
        string declarations = string.Join("\n", Names.Select(name =>
            "G_TIME_" + name == target ? $"CONST {target}=\"locked\""
                : $"PUBLIC G_TIME_{name}=\"old\""));
        List<string> output = [];
        await new Interpreter().RunAsync(declarations
            + "\nTRY\nGETTIME(0,\"20000101\")\nEXCEPT\nPRINT \"caught\"\nENDTRY\n"
            + Snapshot, output.Add);
        Assert.Equal("caught", output[0]);
        Assert.Equal(Names.Select(name => "G_TIME_" + name == target ? "locked" : "old"), output.Skip(1));
    }

    [Fact]
    public async Task ExtraArgumentsHaveNoEffectsAndValidArgumentsRunInOrderOnce()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC calls=0
            TRY
                GETTIME(Tick(),BaseDate(),Tick())
            EXCEPT
                PRINT calls
            ENDTRY
            PRINT gEtTiMe(Tick(),BaseDate())
            PRINT calls
            FUNCTION Tick()
                calls=calls+1
                RESULT=calls
            FEND
            FUNCTION BaseDate()
                calls=calls+1
                RESULT="20000101"
            FEND
            """, output.Add);
        Assert.Equal(["0", "86400", "2"], output);
    }

    [Fact]
    public async Task LocalCalendarAdditionDoesNotApplyDstElapsedTimeRules()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var clock = new CountingClock(new DateTimeOffset(2024, 3, 9, 17, 0, 0, TimeSpan.Zero), zone);
        List<string> output = [];
        await new Interpreter(timeProvider: clock).RunAsync(
            "GETTIME(1)\nPRINT G_TIME_DD\nPRINT G_TIME_HH", output.Add);
        Assert.Equal(["10", "12"], output);
    }

    [Fact]
    public async Task CancellationAfterArgumentsKeepsPreviousSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("""
                OPTION OPTFINALLY
                GETTIME(0,"20000101")
                TRY
                    GETTIME(Offset(),"20250101")
                FINALLY
                    PRINT G_TIME_YY4
                ENDTRY
                FUNCTION Offset()
                    PRINT "cancel"
                    RESULT=1
                FEND
                """, text =>
                {
                    if (text == "cancel")
                    {
                        cancellation.Cancel();
                    }
                    else
                    {
                        output.Add(text);
                    }
                }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["2000"], output);
    }

    [Fact]
    public async Task SnapshotIsPerRunMutableAndVisibleAcrossRoutineCalls()
    {
        var interpreter = new Interpreter();
        List<string> output = [];
        await interpreter.RunAsync("""
            UpdateDate()
            PRINT G_TIME_YY4
            G_TIME_YY4="manual"
            PRINT G_TIME_YY4
            FUNCTION UpdateDate()
                DIM G_TIME_YY4="local"
                GETTIME(0,"20000101")
                PRINT G_TIME_YY4
            FEND
            """, output.Add);
        Assert.Equal(["2000", "2000", "manual"], output);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync("PRINT 1\nPRINT G_TIME_YY4", _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task UnsupportedAdapterKindsKeepTheSnapshotAndTheirContents()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var clock = new CountingClock(default, fail: true);
            var adapter = new ValueAdapter(value);
            List<string> output = [];
            await new Interpreter(windows: adapter, timeProvider: clock).RunAsync("""
                GETTIME(0,"20000101")
                TRY
                    GETTIME(CREATEOLEOBJ("input"),"20250101")
                EXCEPT
                    PRINT G_TIME_YY4
                ENDTRY
                TRY
                    GETTIME(0,CREATEOLEOBJ("input"))
                EXCEPT
                    PRINT G_TIME_YY4
                ENDTRY
                """, output.Add);
            Assert.Equal(["2000", "2000"], output);
            Assert.Equal(0, clock.Calls);
            Assert.True(adapter.Disposed);
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task InvalidArgumentHasItsSourceLine()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("PRINT 1\nGETTIME(0,\"bad\")", _ => { }));
        Assert.Equal(2, error.Line);
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

    private sealed class CountingClock(DateTimeOffset instant,
        TimeZoneInfo? zone = null, bool fail = false) : TimeProvider
    {
        public int Calls
        {
            get;
            private set;
        }

        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            Calls++;
            if (fail)
            {
                throw new InvalidOperationException("時計は参照しません。");
            }

            return instant;
        }
    }
}
