using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class HndToIdContractTests
{
    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"bad\"")]
    [InlineData("9007199254740992")]
    [InlineData("-9007199254740992")]
    [InlineData("1e+300")]
    [InlineData("SPLIT(\"1 2\")")]
    public async Task InvalidHandlesFailBeforeAdapter(string expression)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "\nPRINT HNDTOID(" + expression + ")", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Empty(automation.Arguments);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.9)]
    [InlineData(1.9)]
    [InlineData(-1.9)]
    [InlineData(4294967295d)]
    [InlineData(4294967296d)]
    [InlineData(-2147483649d)]
    [InlineData(9007199254740991d)]
    [InlineData(-9007199254740991d)]
    public async Task ExactRangeValuesReachAdapterWithoutInt32Narrowing(double number)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            "HNDTOID(" + Value.Numeric(number).Display() + ")", _ => { });
        Assert.Equal([Value.Numeric(number)], automation.Arguments);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("123.9")]
    [InlineData("1e+3")]
    [InlineData("9007199254740991")]
    [InlineData("-9007199254740991")]
    public async Task NumericTextUsesSamePrecisionValidation(string text)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            "HNDTOID(\"" + text + "\")", _ => { });
        Assert.Equal([Value.String(text)], automation.Arguments);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteExternalValuesAreRejected(double number)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFunctions.Validate(
            BuiltinFunctionNames.HndToId, [new Value(ValueKind.Number, number)]));
    }

    [Fact]
    public async Task InvalidArityDoesNotEvaluateArguments()
    {
        List<string> output = [];
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(
            "PUBLIC calls=0\nTRY\nHNDTOID(Tick(),Tick())\nEXCEPT\nPRINT calls\nENDTRY\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
        Assert.Empty(automation.Arguments);
    }

    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<Value> Arguments { get; } = [];

        internal bool Disposed
        {
            get;
            private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.HndToId, name);
            Arguments.AddRange(arguments);
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
