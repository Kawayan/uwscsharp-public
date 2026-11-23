using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class CtrlWinContractTests
{
    [Fact]
    public void ExplicitZeroIdIsDistinctFromAnUnsetId()
    {
        WindowsFunctions.Validate(BuiltinFunctionNames.CtrlWin,
            [Value.Numeric(0), Value.Numeric(4)]);
        var request = WindowControlArguments.Read(
            [Value.Numeric(0), Value.Numeric(4)]);
        Assert.Equal(0, request.Id);
        Assert.Throws<InvalidOperationException>(() =>
            WindowsFunctions.Validate(BuiltinFunctionNames.CtrlWin,
                [Value.Empty, Value.Numeric(4)]));
    }

    [Theory]
    [InlineData("CTRLWIN(EMPTY,HIDE)")]
    [InlineData("CTRLWIN(1,EMPTY)")]
    [InlineData("CTRLWIN(NULL,HIDE)")]
    [InlineData("CTRLWIN(1,NULL)")]
    [InlineData("CTRLWIN(2147483648,HIDE)")]
    [InlineData("CTRLWIN(1,-1)")]
    [InlineData("CTRLWIN(1,4294967296)")]
    [InlineData("CTRLWIN(1,32769,-1)")]
    [InlineData("CTRLWIN(1,32769,4294967296)")]
    [InlineData("CTRLWIN(1,32769,0,2147483648)")]
    [InlineData("CTRLWIN(1,32769,0,-2147483649)")]
    [InlineData("CTRLWIN(1,32769,NULL)")]
    [InlineData("CTRLWIN(1,32769,0,NULL)")]
    [InlineData("CTRLWIN(1,1e300)")]
    public async Task InvalidValuesCannotReachTheAdapter(string call)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "\n" + call, _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Empty(automation.Calls);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("CTRLWIN(1,32769)", 32769, 0, 0)]
    [InlineData("CTRLWIN(1,32769,4294967295,-2147483648)",
        32769, uint.MaxValue, int.MinValue)]
    [InlineData("CTRLWIN(1,4294967295,0,2147483647)",
        uint.MaxValue, 0, int.MaxValue)]
    [InlineData("CTRLWIN(1,32769,,123)", 32769, 0, 123)]
    [InlineData("CTRLWIN(1,32769,123,)", 32769, 123, 0)]
    [InlineData("CTRLWIN(1,32769,EMPTY,EMPTY)", 32769, 0, 0)]
    [InlineData("CTRLWIN(1,32769.9,123.9,-456.9)", 32769, 123, -456)]
    [InlineData("CTRLWIN(1,\"32769\",\"123\",\"-456\")",
        32769, 123, -456)]
    [InlineData("CTRLWIN(1,0)", 0, 0, 0)]
    [InlineData("CTRLWIN(1,HIDE,123,456)", 4, 123, 456)]
    public async Task MessageValuesAndOmissionsRetainTheirWidths(
        string call, long command, long wParam, int lParam)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT " + call, output.Add);
        var request = WindowControlArguments.Read(
            Assert.Single(automation.Calls));
        Assert.Equal(1, request.Id);
        Assert.Equal((uint)command, request.Command);
        Assert.Equal((uint)wParam, request.WParam);
        Assert.Equal(lParam, request.LParam);
        Assert.Equal([""], output);
        Assert.True(automation.Disposed);
    }

    [Theory]
    [InlineData("CTRLWIN(Tick())")]
    [InlineData("CTRLWIN(,Tick())")]
    [InlineData("CTRLWIN(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task ArityFailureDoesNotEvaluateArguments(string call)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT calls\nENDTRY\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND",
            output.Add);
        Assert.Equal(["0"], output);
        Assert.Empty(automation.Calls);
    }

    private sealed class Automation : IWindowsAutomation,
        IWindowsAutomationSession
    {
        internal List<IReadOnlyList<Value>> Calls
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
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.CtrlWin, name);
            Calls.Add(arguments);
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
