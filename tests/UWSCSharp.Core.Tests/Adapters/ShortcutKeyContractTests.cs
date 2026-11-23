using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ShortcutKeyContractTests
{
    [Fact]
    public async Task ExpandsOnlyStandaloneRawLettersAndDigits()
    {
        var adapter = new Adapter();
        await new Interpreter(windows: adapter).RunAsync("""
            a = 7
            SCKEY(0, a, (a), 1, "1", VK_CTRL)
            """, _ => { });
        Assert.Equal([0, 65, 7, 49, 1, 17], adapter.Arguments);
    }

    [Theory]
    [InlineData("SCKEY()")]
    [InlineData("SCKEY(side())")]
    [InlineData("SCKEY(0, side(),)")]
    [InlineData("SCKEY(, side())")]
    public async Task RejectsMissingKeysBeforeArgumentSideEffects(string expression)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($"""
            TRY
                {expression}
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION side()
                PRINT "side"
                RESULT=65
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Empty(adapter.Arguments);
    }

    [Theory]
    [InlineData("SCKEY(0, -1)")]
    [InlineData("SCKEY(0, 65536)")]
    [InlineData("SCKEY(0, NULL)")]
    [InlineData("SCKEY(0, \"A\")")]
    public async Task RejectsInvalidKeysBeforeAdapter(string expression)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($"""
            TRY
                {expression}
            EXCEPT
                PRINT "caught"
            ENDTRY
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Empty(adapter.Arguments);
    }


    private sealed class Adapter : IWindowsAutomation,
        IWindowsAutomationSession
    {
        internal int Sessions;

        internal int[] Arguments { get; private set; } = [];

        public IWindowsAutomationSession CreateSession()
        {
            Sessions++;
            return this;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Arguments = [.. arguments.Select(NumericOperations.Integer)];
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
