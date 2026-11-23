using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PrintValueBoundaryTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonFiniteAdapterValueFailsOnPrintLineWithoutCallingOutput(double number)
    {
        var output = new List<string>();
        var value = new Value(ValueKind.Number, Number: number);
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new Source(value)).RunAsync(
                "\nPRINT GETSTR(1)", output.Add));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(ValueKind.Omitted)]
    [InlineData((ValueKind)12345)]
    public async Task InvalidAdapterTagFailsWithoutCallingOutput(ValueKind kind)
    {
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: new Source(new Value(kind))).RunAsync(
                "\nPRINT GETSTR(1)", output.Add));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
    }

    private sealed class Source(Value value) : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult(value);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
