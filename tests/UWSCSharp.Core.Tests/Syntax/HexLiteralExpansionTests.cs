using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class HexLiteralExpansionTests
{
    private sealed class Windows : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<Value> Captured { get; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Captured.AddRange(arguments);
            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("$100000000", 4294967296d)]
    [InlineData("$FFFFFFFF", 4294967295d)]
    [InlineData("$1FFFFFFFFFFFFF", 9007199254740991d)]
    [InlineData("$20000000000001", 9007199254740992d)]
    [InlineData("$7FFFFFFFFFFFFFFF", 9223372036854775808d)]
    [InlineData("$8000000000000000", -9223372036854775808d)]
    [InlineData("$FFFFFFFFFFFFFFFF", -1d)]
    [InlineData("-$FFFFFFFFFFFFFFFF", 1d)]
    [InlineData("-$100000000", -4294967296d)]
    public void LiteralUsesSignedInt64ThenDouble(string expression, double expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).AsNumber());
    }

    [Theory]
    [InlineData("$10000000000000000")]
    public void OutOfRangeLiteralFailsBeforeExecution(string expression)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("PRINT " + expression));
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public async Task WideLiteralReachesWindowBoundaryWithoutInt32Truncation()
    {
        var windows = new Windows();
        await new Interpreter(windows: windows).RunAsync(
            "HNDTOID($100000000)", _ => { });
        Assert.Equal(4294967296d, Assert.Single(windows.Captured).AsNumber());
    }
}
