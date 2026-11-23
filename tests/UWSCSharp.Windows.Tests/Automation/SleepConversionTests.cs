using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SleepConversionTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("ar-SA")]
    public async Task ConversionIsIndependentOfCultureAndWindowsAdapter(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            const string source = "SLEEP(\"1e-3\")\nTRY\nSLEEP(\"0,001\")\n"
                + "EXCEPT\nPRINT \"caught\"\nENDTRY";
            var plain = new Host();
            await new Interpreter().RunAsync(source, plain);
            Assert.Equal([TimeSpan.FromMilliseconds(1)], plain.Waits);
            Assert.Equal(["caught"], plain.Output);
            var windows = new Host();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(source, windows);
            Assert.Equal(plain.Waits, windows.Waits);
            Assert.Equal(plain.Output, windows.Output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task NativeTrueIsNegativeAndRejectedWhileLiteralTrueWaitsOneSecond()
    {
        var host = new Host();
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            o=CREATEOLEOBJ("Scripting.Dictionary")
            o.Add("x",1)
            TRY
                SLEEP(o.Exists("x"))
            EXCEPT
                PRINT "caught"
            ENDTRY
            SLEEP(o.Exists("missing"))
            SLEEP(TRUE)
            PRINT "done"
            """, host);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1)], host.Waits);
        Assert.Equal(["caught", "done"], host.Output);
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
}
