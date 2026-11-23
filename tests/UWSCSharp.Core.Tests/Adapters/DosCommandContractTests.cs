using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DosCommandContractTests
{
    [Theory]
    [InlineData("DOSCMD()")]
    [InlineData("DOSCMD(,Tick())")]
    [InlineData("DOSCMD(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT calls\nENDTRY\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
    }

    [Theory]
    [InlineData("DOSCMD(EMPTY)")]
    [InlineData("DOSCMD(NULL)")]
    [InlineData("DOSCMD(123)")]
    [InlineData("DOSCMD(\"echo x\",NULL)")]
    [InlineData("DOSCMD(\"echo x\",-1)")]
    [InlineData("DOSCMD(\"echo x\",-2)")]
    [InlineData("DOSCMD(\"echo x\",,,2147483648)")]
    public async Task InvalidValuesAreRejectedBeforeAdapter(string source)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(source, _ => { }));
        Assert.False(adapter.Invoked);
    }

    [Fact]
    public void NormalizationAllowsEmptyCommandsAndIndependentOmissions()
    {
        var empty = DosCommandRequest.Read([Value.String("")]);
        Assert.True(empty.CaptureOutput);
        Assert.False(empty.NoWait);
        var request = DosCommandRequest.Read([Value.String("  echo one\r\n\r\n echo two \n"),
            new Value(ValueKind.Omitted), Value.Numeric(1), Value.Numeric(3)]);
        Assert.Equal("echo one & echo two", request.Command);
        Assert.True(request.ShowWindow);
        Assert.True(request.UnicodeOutput);
        Assert.True(request.ReturnExitCode);
        Assert.False(request.CaptureOutput);
        Assert.Throws<InvalidOperationException>(() =>
            DosCommandRequest.Read([Value.String("echo\0ignored")]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(255, true)]
    [InlineData(256, true)]
    public void NonnegativeWaitFlagAndNegativeShowFlagAreAccepted(
        int noWait, bool expected)
    {
        var request = DosCommandRequest.Read([Value.String("echo flag"),
            Value.Numeric(noWait), Value.Numeric(-1)]);
        Assert.Equal(expected, request.NoWait);
        Assert.True(request.ShowWindow);
    }

    [Fact]
    public async Task BooleanUnicodeModeAndStringResultReachInterpreter()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            "PRINT DOSCMD(\"echo x\",,,TRUE)", output.Add);
        Assert.True(adapter.Invoked);
        Assert.Equal(["result"], output);
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
            Assert.Equal(BuiltinFunctionNames.DosCmd, name);
            var request = DosCommandRequest.Read(arguments);
            Assert.Equal(1, request.OutputMode);
            Invoked = true;
            return ValueTask.FromResult(Value.String("result"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
