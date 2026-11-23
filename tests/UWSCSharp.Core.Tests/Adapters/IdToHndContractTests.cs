using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class IdToHndContractTests
{
    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"bad\"")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e+300")]
    [InlineData("SPLIT(\"1 2\")")]
    public async Task InvalidIdsFailBeforeAdapterAndKeepSourceLine(string expression)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "\nPRINT IDTOHND(" + expression + ")", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Empty(automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("FALSE")]
    [InlineData("TRUE")]
    [InlineData("-1")]
    [InlineData("1.9")]
    [InlineData("-0.9")]
    [InlineData("\"1e+0\"")]
    public async Task ExplicitFiniteIdsKeepTheExistingDispatchPath(string expression)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT IDTOHND(" + expression + ")", output.Add);
        Assert.Equal(["7"], output);
        Assert.Equal([BuiltinFunctionNames.IdToHnd], automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonfiniteAdapterValuesCannotReachHandleLookup(double number)
    {
        var automation = new Automation
        {
            ReturnedId = new Value(ValueKind.Number, number)
        };
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "PRINT IDTOHND(GETID(\"owned\"))", _ => { }));
        Assert.Equal([BuiltinFunctionNames.GetId], automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("IDTOHND(Tick(),Tick())")]
    [InlineData("MMV(Tick())")]
    [InlineData("GETID(Tick(),Tick(),Tick(),Tick(),Tick())")]
    [InlineData("KBD(,Tick())")]
    public async Task ArityFailureDoesNotEvaluateArguments(string call)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PUBLIC calls=0\nTRY\n" + call + "\nEXCEPT\nPRINT calls\nENDTRY\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
        Assert.Empty(automation.Calls);
    }

    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<string> Calls
        {
            get;
        } = [];

        internal Value ReturnedId
        {
            get;
            init;
        } = Value.Numeric(1);

        internal bool Disposed
        {
            get;
            private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(name);
            return ValueTask.FromResult(name == BuiltinFunctionNames.GetId
                ? ReturnedId : Value.Numeric(7));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
