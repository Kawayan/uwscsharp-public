using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class TimeRandomAuditTests
{
    private sealed class CancellingClock(CancellationTokenSource cancellation) : TimeProvider
    {
        private int calls;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            if (++calls == 1)
            {
                cancellation.Cancel();
            }

            return new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task ClockCancellationKeepsPreviousTimeSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        var runner = new Interpreter(timeProvider: new CancellingClock(cancellation));
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("""
                OPTION OPTFINALLY
                GETTIME(0,"20000101")
                TRY
                    GETTIME()
                FINALLY
                    PRINT G_TIME_YY4
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["2000"], output);
    }

    [Theory]
    [InlineData("1.5", "20000228", "2000", "29", "12", "5140800")]
    [InlineData("-0.5", "20000101", "1999", "31", "12", "-43200")]
    [InlineData("1e-3", "20000101", "2000", "01", "00", "86")]
    public async Task TimeOffsetUsesNumericStrings(string offset, string date,
        string year, string day, string hour, string seconds)
    {
        var output = new List<string>();
        await new Interpreter().RunAsync(
            $"PRINT GETTIME(\"{offset}\",\"{date}\")\nPRINT G_TIME_YY4\nPRINT G_TIME_DD2\nPRINT G_TIME_HH2",
            output.Add);
        Assert.Equal([seconds, year, day, hour], output);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("1e999")]
    [InlineData("NaN")]
    public async Task InvalidTimeOffsetKeepsSnapshot(string offset)
    {
        var output = new List<string>();
        await new Interpreter().RunAsync(
            "GETTIME(0,\"20000101\")\nTRY\nGETTIME(\"" + offset
            + "\",\"20250101\")\nEXCEPT\nPRINT G_TIME_YY4\nENDTRY", output.Add);
        Assert.Equal(["2000"], output);
    }

    [Theory]
    [InlineData("1000000", "0", "31379")]
    [InlineData("0", "0", "0")]
    [InlineData("-1", "0", "134775813")]
    [InlineData("2147483647", "0", "67387906")]
    [InlineData("-2147483648", "0", "67387907")]
    public async Task RandomConvertsBoundAndUsesUnsignedProduct(string bound,
        string first, string second)
    {
        var output = new List<string>();
        await new Interpreter(new UwscRandom(0)).RunAsync(
            $"PRINT RANDOM(\"{bound}\")\nPRINT RANDOM(\"{bound}\")", output.Add);
        Assert.Equal([first, second], output);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task InvalidRandomBoundDoesNotConsumeState(string bound)
    {
        var random = new UwscRandom(0);
        var runner = new Interpreter(random);
        await Assert.ThrowsAsync<ScriptException>(() =>
            runner.RunAsync($"PRINT RANDOM(\"{bound}\")", _ => { }));
        Assert.Equal(1.0 / 4294967296.0, random.Next());
    }

    [Fact]
    public async Task ZeroStringBoundConsumesOneRandomStep()
    {
        var random = new UwscRandom(0);
        await new Interpreter(random).RunAsync("RANDOM(\"0\")", _ => { });
        Assert.Equal(134775814.0 / 4294967296.0, random.Next());
    }

    [Fact]
    public async Task CancelledArgumentDoesNotConsumeRandomState()
    {
        using var cancellation = new CancellationTokenSource();
        var random = new UwscRandom(0);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(random).RunAsync("""
                RANDOM(Bound())
                FUNCTION Bound()
                    PRINT "cancel"
                    RESULT = 100
                FEND
                """, _ => cancellation.Cancel(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1.0 / 4294967296.0, random.Next());
    }
}
