using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class AcwContractTests
{
    [Theory]
    [InlineData("ACW(EMPTY)")]
    [InlineData("ACW(NULL)")]
    [InlineData("ACW(NOTHING)")]
    [InlineData("ACW(\"\")")]
    [InlineData("ACW(\"bad\")")]
    [InlineData("ACW(2147483648)")]
    [InlineData("ACW(-2147483649)")]
    [InlineData("ACW(1,1e+300)")]
    [InlineData("ACW(1,NULL)")]
    [InlineData("ACW(1,,,-1)")]
    [InlineData("ACW(1,,,,-1)")]
    [InlineData("ACW(1,,,,,-1)")]
    public async Task InvalidValuesFailBeforeAdapterAndKeepSourceLine(string call)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync("\n" + call, _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Empty(automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("ACW(0)")]
    [InlineData("ACW(-1)")]
    [InlineData("ACW(1, -40, -60)")]
    [InlineData("ACW(1, , , 0, 0)")]
    [InlineData("ACW(1, , , 400, 300, 0)")]
    [InlineData("ACW(1.9, 40.9, 60.9, 400.9, 300.9, 20.9)")]
    [InlineData("ACW(\"1\", \"40\", \"60\", \"400\", \"300\", \"20\")")]
    [InlineData("ACW(TRUE, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY)")]
    public async Task AdoptedNumericAndOmittedArgumentsReachAdapter(string call)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(call, _ => { });
        Assert.Equal([BuiltinFunctionNames.Acw], automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("ACW()")]
    [InlineData("ACW(,Tick())")]
    [InlineData("ACW(Tick(),Tick(),Tick(),Tick(),Tick(),Tick(),Tick())")]
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
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
