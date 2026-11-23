using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class GetStrContractTests
{
    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.GetStr, name);
            Calls++;
            return ValueTask.FromResult(Value.String("value"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("GETSTR(1)")]
    [InlineData("GETSTR(1,1,STR_EDIT,TRUE)")]
    [InlineData("GETSTR(1,-2,STR_STATIC,FALSE)")]
    [InlineData("GETSTR(1,0,STR_STATIC,TRUE)")]
    [InlineData("GETSTR(1,2,STR_STATUS,TRUE)")]
    [InlineData("GETSTR(1,1,STR_ACC_EDIT,TRUE)")]
    [InlineData("GETSTR(1,1,STR_ACC_STATIC,TRUE)")]
    [InlineData("GETSTR(1,-2,STR_ACC_CELL,TRUE)")]
    [InlineData("GETSTR(0,0,0,TRUE)")]
    [InlineData("GETSTR(1,EMPTY,EMPTY,EMPTY)")]
    public async Task SupportedKindsAndMoveFlagReachTheAdapter(string source)
    {
        var automation = new Automation();
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync("PRINT " + source, output.Add);
        Assert.Equal(1, automation.Calls);
        Assert.Equal(["value"], output);
    }

    [Theory]
    [InlineData("GETSTR()")]
    [InlineData("GETSTR(,1)")]
    [InlineData("GETSTR(1,1,0,0,0)")]
    [InlineData("GETSTR(1,1,-1)")]
    [InlineData("GETSTR(1,1,6)")]
    [InlineData("GETSTR(1,1,0,-1)")]
    [InlineData("GETSTR(1,1,0,2)")]
    [InlineData("GETSTR(1,1,0,1e20)")]
    [InlineData("GETSTR(1,-2147483648,3)")]
    [InlineData("GETSTR(1,-2147483648,4)")]
    [InlineData("GETSTR(1,-2147483648,5)")]
    public async Task InvalidArgumentsNeverReachTheAdapter(string source)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(source, _ => { }));
        Assert.Equal(0, automation.Calls);
    }
}
