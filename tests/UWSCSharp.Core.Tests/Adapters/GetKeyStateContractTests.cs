using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class GetKeyStateContractTests
{
    [Theory]
    [InlineData("VK_A", 65)]
    [InlineData("VK_RRETURN", 901)]
    [InlineData("VK_RENTER", 901)]
    [InlineData("TGL_NUMLOCK", 10000)]
    [InlineData("TGL_CAPSLOCK", 10001)]
    [InlineData("TGL_SCROLLLOCK", 10002)]
    [InlineData("TGL_KANALOCK", 10003)]
    [InlineData("TGL_IME", 10004)]
    [InlineData("EMPTY", 0)]
    [InlineData("\"65\"", 65)]
    public async Task RegisteredConstantsRouteToAdapter(string expression, int code)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            $"PRINT GETKEYSTATE({expression})", output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal(code, NumericOperations.Integer(Assert.Single(adapter.Values)));
    }

    [Theory]
    [InlineData("GETKEYSTATE()")]
    [InlineData("GETKEYSTATE(,Tick())")]
    [InlineData("GETKEYSTATE(Tick(),Tick())")]
    public async Task ArityIsCheckedBeforeEvaluation(string call)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("PUBLIC calls=0\nTRY\n"
            + call + "\nEXCEPT\nPRINT calls\nENDTRY\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=65\nFEND", output.Add);
        Assert.Equal(["0"], output);
        Assert.Empty(adapter.Values);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("256")]
    [InlineData("900")]
    [InlineData("902")]
    [InlineData("10005")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"bad\"")]
    [InlineData("2147483648")]
    public async Task InvalidCodesDoNotReachAdapter(string expression)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                $"GETKEYSTATE({expression})", _ => { }));
        Assert.Empty(adapter.Values);
    }


    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal IReadOnlyList<Value> Values { get; private set; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.GetKeyState, name);
            Values = arguments;
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
