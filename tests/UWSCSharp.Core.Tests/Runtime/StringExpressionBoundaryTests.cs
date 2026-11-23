using System.Globalization;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class StringExpressionBoundaryTests
{
    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.NaN, true)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.PositiveInfinity, true)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(double.NegativeInfinity, true)]
    public async Task InvalidAdapterNumberFailsAtConcatenationAndReleasesSession(
        double number, bool valueFirst)
    {
        var source = new Source(new Value(ValueKind.Number, number));
        var output = new List<string>();
        string expression = valueFirst ? "GETSTR(1)+\"x\"" : "\"x\"+GETSTR(1)";
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: source).RunAsync(
                "\nDIM text=" + expression + "\nPRINT text", output.Add));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
        Assert.Equal(1, source.Calls);
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(false, "a2")]
    [InlineData(true, "caught")]
    public async Task BothOperandsRunOnceInOrderEvenIfConversionFails(
        bool invalid, string expected)
    {
        string result = invalid ? "NULL" : "\"a\"";
        string script = "PUBLIC trace=\"\"\nTRY\nPRINT FirstText()+SecondText()\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT trace\n"
            + "FUNCTION FirstText()\ntrace=trace+\"L\"\nRESULT=" + result
            + "\nFEND\nFUNCTION SecondText()\ntrace=trace+\"R\"\nRESULT=2\nFEND";
        var output = new List<string>();
        await new Interpreter().RunAsync(script, output.Add);
        Assert.Equal([expected, "LR"], output);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-EG")]
    public async Task ConcatenationKeepsInvariantNumericText(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var output = new List<string>();
            await new Interpreter().RunAsync("PRINT \"x\"+1.25", output.Add);
            Assert.Equal(["x1.25"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class Source(Value value) : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls
        {
            get; private set;
        }

        internal bool Disposed
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Calls++;
            return ValueTask.FromResult(value);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
