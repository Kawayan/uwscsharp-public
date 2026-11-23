using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class GetIdContractTests
{
    [Theory]
    [InlineData("GETID(\"parent\",,0.01,\"child\")", "child")]
    [InlineData("GETID(\"parent\",,,\"child\")", "child")]
    [InlineData("GETID(\"parent\",,0.01,123)", "123")]
    [InlineData("GETID(\"parent\",,0.01,TRUE)", "1")]
    [InlineData("GETID(\"parent\",,0.01,EMPTY)", "")]
    [InlineData("GETID(\"parent\",,0.01,)", "")]
    public async Task FourthArgumentIsAChildTitle(string call, string expected)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync(call, _ => { });
        Assert.Equal(BuiltinFunctionNames.GetId, automation.Name);
        Assert.Equal(4, automation.Arguments.Count);
        Assert.Equal(expected, new CallArguments(automation.Arguments).Str(3));
    }

    [Theory]
    [InlineData("GETID()")]
    [InlineData("GETID(,Tick(),Tick(),Tick())")]
    [InlineData("GETID(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PUBLIC calls=0\nTRY\n" + call + "\nEXCEPT\nPRINT calls\nENDTRY\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
        Assert.Null(automation.Name);
    }

    [Theory]
    [InlineData("GETID(\"parent\",,-2,\"child\")")]
    [InlineData("GETID(\"parent\",,-0.5,\"child\")")]
    [InlineData("GETID(\"parent\",,NULL,\"child\")")]
    public async Task InvalidWaitFailsBeforeAdapter(string call)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(call, _ => { }));
        Assert.Null(automation.Name);
    }

    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal string? Name
        {
            get;
            private set;
        }

        internal IReadOnlyList<Value> Arguments
        {
            get;
            private set;
        } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Name = name;
            Arguments = arguments;
            return ValueTask.FromResult(Value.Numeric(-1));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
