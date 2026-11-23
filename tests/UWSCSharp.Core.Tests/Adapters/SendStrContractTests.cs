using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class SendStrContractTests
{
    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.SendStr, name);
            Calls++;
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("SENDSTR(1,\"x\")")]
    [InlineData("SENDSTR(1,\"x\",1,0,0)")]
    [InlineData("SENDSTR(1,\"x\",0,0,TRUE)")]
    [InlineData("SENDSTR(1,\"x\",-2,1,1)")]
    [InlineData("SENDSTR(1,\"x\",1,2,2)")]
    [InlineData("SENDSTR(1,\"x\",1,1,STR_ACC_CELL)")]
    [InlineData("SENDSTR(1,\"x\",EMPTY,EMPTY,EMPTY)")]
    [InlineData("SENDSTR(0,\"日本語\",1,1,5)")]
    public async Task ProviderSelectorsReachTheAdapter(string source)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(source, _ => { });
        Assert.Equal(1, automation.Calls);
    }

    [Theory]
    [InlineData("SENDSTR()")]
    [InlineData("SENDSTR(1)")]
    [InlineData("SENDSTR(1,,1)")]
    [InlineData("SENDSTR(1,\"x\",1,0,0,0)")]
    [InlineData("SENDSTR(1,\"x\",1,-1)")]
    [InlineData("SENDSTR(1,\"x\",1,3)")]
    [InlineData("SENDSTR(1,\"x\",1,0,-1)")]
    [InlineData("SENDSTR(1,\"x\",1,0,3)")]
    [InlineData("SENDSTR(1,\"x\",1,0,4)")]
    [InlineData("SENDSTR(1,\"x\",1,0,6)")]
    [InlineData("SENDSTR(1,\"x\",1,0,1e20)")]
    public async Task InvalidSelectorsNeverReachTheAdapter(string source)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(source, _ => { }));
        Assert.Equal(0, automation.Calls);
    }
}
