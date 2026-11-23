using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PowerShellContractTests
{
    [Theory]
    [InlineData("POWERSHELL()")]
    [InlineData("POWERSHELL(,Tick())")]
    [InlineData("POWERSHELL(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT calls\nENDTRY\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
    }

    [Theory]
    [InlineData("POWERSHELL(EMPTY)")]
    [InlineData("POWERSHELL(NULL)")]
    [InlineData("POWERSHELL(123)")]
    [InlineData("POWERSHELL(\"1\",NULL)")]
    [InlineData("POWERSHELL(\"1\",,2147483648)")]
    public async Task InvalidValuesDoNotReachAdapter(string source)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(source, _ => { }));
        Assert.False(adapter.Invoked);
    }

    [Fact]
    public void ScriptAndIndependentOmissionsArePreserved()
    {
        const string command = "  $x='a'\r\n$x \n";
        var request = PowerShellRequest.Read([Value.String(command),
            new Value(ValueKind.Omitted), Value.Numeric(2)]);
        Assert.Equal(command, request.Command);
        Assert.False(request.NoWait);
        Assert.Equal(2, request.Display);
        Assert.False(request.CaptureOutput);
        Assert.True(PowerShellRequest.Read([Value.String("")]).CaptureOutput);
        Assert.Throws<InvalidOperationException>(() =>
            PowerShellRequest.Read([Value.String("1\0ignored")]));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("3")]
    public async Task NonzeroDisplayOtherThanTwoRoutesToNormalWindow(string display)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            $"PRINT POWERSHELL(\"1\",,{display})", output.Add);
        Assert.Equal(["result"], output);
        Assert.True(adapter.Invoked);
    }


    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal bool Invoked
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.PowerShell, name);
            Assert.Equal(1, PowerShellRequest.Read(arguments).Display);
            Invoked = true;
            return ValueTask.FromResult(Value.String("result"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
