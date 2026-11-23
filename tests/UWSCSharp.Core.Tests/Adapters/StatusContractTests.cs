using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class StatusContractTests
{
    [Fact]
    public void EmptyIdIsNotAnExplicitForegroundId()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsFunctions.Validate(BuiltinFunctionNames.Status,
                [Value.Empty, Value.Numeric(21)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.9)]
    [InlineData(-1)]
    [InlineData(1.9)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ExplicitIdsAcceptAllSupportedItems(double id)
    {
        for (int item = 1; item <= 21; item++)
        {
            WindowsFunctions.Validate(BuiltinFunctionNames.Status,
                [Value.Numeric(id), Value.Numeric(item)]);
        }
    }

    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("\"\"")]
    [InlineData("\"bad\"")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e+300")]
    public async Task InvalidIdFailsBeforeAdapterAndRetainsSourceLine(string expression)
    {
        var automation = new Automation();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "\nPRINT STATUS(" + expression + ",ST_ISID)", _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Equal(0, automation.Calls);
    }

    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("0")]
    [InlineData("22")]
    [InlineData("\"bad\"")]
    public async Task InvalidItemFailsBeforeAdapter(string expression)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "PRINT STATUS(-1," + expression + ")", _ => { }));
        Assert.Equal(0, automation.Calls);
    }

    private sealed class Automation : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls
        {
            get;
            private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(Value.Numeric(0));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
