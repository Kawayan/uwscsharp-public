using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExecContractTests
{
    [Theory]
    [InlineData("EXEC()")]
    [InlineData("EXEC(,Tick())")]
    [InlineData("EXEC(Tick(),Tick(),Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT calls\nENDTRY\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
    }

    [Theory]
    [InlineData("EXEC(\"\")")]
    [InlineData("EXEC(EMPTY)")]
    [InlineData("EXEC(NULL)")]
    [InlineData("EXEC(123)")]
    [InlineData("EXEC(\"app\",NULL)")]
    [InlineData("EXEC(\"app\",,2147483648)")]
    [InlineData("EXEC(\"app\",,,, -1)")]
    public async Task InvalidValuesAreRejected(string source)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(source, _ => { }));
        Assert.False(adapter.Invoked);
    }

    [Fact]
    public async Task RegisteredFunctionRoutesToWindowsAdapter()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            "PRINT EXEC(\"app args\",,10,,0,200)", output.Add);
        Assert.True(adapter.Invoked);
        Assert.Equal(["42"], output);
    }


    [Fact]
    public void DefaultsAndIndependentOmissions()
    {
        var basic = ExecArguments.Read([Value.String("app")]);
        Assert.False(basic.Wait);
        Assert.False(basic.HasGeometry);
        var specified = ExecArguments.Read([
            Value.String("app args"), Value.Numeric(1), new Value(ValueKind.Omitted),
            Value.Numeric(-25.9), Value.Numeric(0), Value.Numeric(400)]);
        Assert.True(specified.Wait);
        Assert.Null(specified.X);
        Assert.Equal(-25, specified.Y);
        Assert.Equal(0, specified.Width);
        Assert.Equal(400, specified.Height);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("   ")]
    public void InvalidCommandCannotBeTruncated(string text)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsFunctions.Validate("EXEC", [Value.String(text)]));
        Assert.Throws<InvalidOperationException>(() =>
            WindowsFunctions.Validate("EXEC", [Value.String(new string('a', 32767))]));
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
            Assert.Equal(BuiltinFunctionNames.Exec, name);
            Invoked = true;
            return ValueTask.FromResult(Value.Numeric(42));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
