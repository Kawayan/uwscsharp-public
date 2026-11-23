using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class MmvContractTests
{
    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.Mmv, name);
            Calls++;
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("MMV(10,20)")]
    [InlineData("MMV(-10,-20)")]
    [InlineData("MMV(EMPTY,EMPTY)")]
    [InlineData("MMV(10.9,20.9,0.9)")]
    [InlineData("MMV(2147483647,-2147483648)")]
    [InlineData("MMV(10,20,2147483647)")]
    [InlineData("MMV(10,20,)")]
    public async Task ValidCoordinatesAndOptionalDelayReachTheAdapter(string source)
    {
        var automation = new Automation();
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync(source, output.Add);
        Assert.Equal(1, automation.Calls);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData("MMV()")]
    [InlineData("MMV(10)")]
    [InlineData("MMV(,20)")]
    [InlineData("MMV(10,)")]
    [InlineData("MMV(10,20,0,1)")]
    [InlineData("MMV(\"bad\",20)")]
    [InlineData("MMV(1e20,20)")]
    [InlineData("MMV(10,-1e20)")]
    [InlineData("MMV(10,20,-1)")]
    [InlineData("MMV(10,20,2147483648)")]
    public async Task InvalidArgumentsNeverReachTheInputAdapter(string source)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(source, _ => { }));
        Assert.Equal(0, automation.Calls);
    }
}
