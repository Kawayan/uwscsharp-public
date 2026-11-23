using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class BtnContractTests
{
    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<Value[]> Calls { get; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.Btn, name);
            Calls.Add([.. arguments]);
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("BTN(TOUCH)")]
    [InlineData("BTN(touch,DOWN)")]
    [InlineData("BTN(7,UP,10,20,0)")]
    [InlineData("BTN(7,EMPTY,EMPTY,EMPTY)")]
    [InlineData("BTN(7.9,1.9,10.9,20.9,0.9)")]
    [InlineData("BTN(7,,10,20)")]
    [InlineData("BTN(HWHEEL,-1.25)")]
    [InlineData("BTN(WHEEL,17895697)")]
    [InlineData("BTN(EMPTY)")]
    [InlineData("BTN(LEFT,CLICK,,,5)")]
    public async Task AcceptedArgumentsReachTheAdapter(string source)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(source, _ => { });
        Assert.Single(automation.Calls);
    }

    [Theory]
    [InlineData("BTN()")]
    [InlineData("BTN(,0)")]
    [InlineData("BTN(8)")]
    [InlineData("BTN(-1)")]
    [InlineData("BTN(TOUCH,3)")]
    [InlineData("BTN(TOUCH,-1)")]
    [InlineData("BTN(TOUCH,0,10)")]
    [InlineData("BTN(TOUCH,0,,20)")]
    [InlineData("BTN(TOUCH,0,10,20,-1)")]
    [InlineData("BTN(TOUCH,0,10,20,2147483648)")]
    [InlineData("BTN(TOUCH,0,10,20,0,1)")]
    [InlineData("BTN(WHEEL,\"bad\",10,20)")]
    [InlineData("BTN(HWHEEL,1e20,10,20)")]
    [InlineData("BTN(WHEEL,-1e20,10,20)")]
    [InlineData("DIM TOUCH=1")]
    [InlineData("CONST TOUCH=1")]
    [InlineData("TOUCH=1")]
    public async Task InvalidCallsAndReservedAssignmentsHaveNoInputSideEffects(string source)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(source, _ => { }));
        Assert.Empty(automation.Calls);
    }

    [Fact]
    public async Task TouchConstantHasTheOriginalNumericValue()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("PRINT TOUCH", output.Add);
        Assert.Equal(["7"], output);
    }
}
