using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ReferenceValueTests
{
    [Fact]
    public async Task NothingRemainsDistinctFromEmptyAndNull()
    {
        Assert.Equal(["NOTHING", "1", "0", "0", "different"], await Run("""
            DIM n=NOTHING
            PRINT n
            PRINT n=NOTHING
            PRINT n=EMPTY
            PRINT n=NULL
            SELECT n
            CASE EMPTY
                PRINT "empty"
            CASE NULL
                PRINT "null"
            CASE NOTHING
                PRINT "different"
            SELEND
            """));
    }

    [Fact]
    public void ObjectEqualityUsesIdentityInsteadOfObjectEquals()
    {
        var first = new EqualObject();
        var second = new EqualObject();
        var variables = new Dictionary<string, Value>
        {
            ["a"] = Value.FromObject(first),
            ["b"] = Value.FromObject(first),
            ["c"] = Value.FromObject(second)
        };
        Assert.Equal(1, ExpressionEvaluator.Evaluate("a=b", variables).Number);
        Assert.Equal(0, ExpressionEvaluator.Evaluate("a=c", variables).Number);
        Assert.Equal(1, ExpressionEvaluator.Evaluate("a<>NOTHING", variables).Number);
        Assert.Same(first, variables["a"].AsObject());
        Assert.Equal(Value.Nothing, Value.FromObject(null));
    }

    private sealed class EqualObject
    {
        public override bool Equals(object? obj) => obj is EqualObject;

        public override int GetHashCode() => 0;
    }

    [Theory]
    [InlineData(-2147352572)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void ErrorCodeKeepsTagAndRejectsNumericConversion(int code)
    {
        var error = Value.FromError(code);
        Assert.Equal(ValueKind.Error, error.Kind);
        Assert.Equal(code, error.AsErrorCode());
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Number(error));
        Assert.Throws<InvalidOperationException>(() => error.Display());
        Assert.Throws<InvalidOperationException>(() => error.AsObject());
    }

    [Fact]
    public void NothingIsRejectedByNumericOperations()
    {
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Number(Value.Nothing));
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate("NOTHING<NOTHING"));
    }

    private sealed class Values(Value value) : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult(value);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AdapterErrorCanBeStoredCopiedAndPrinted()
    {
        var output = new List<string>();
        await new Interpreter(windows: new Values(Value.FromError(-7))).RunAsync("""
            DIM errorValue=GETSTR(1)
            DIM values[]=errorValue
            PRINT values[0]
            """, output.Add);
        Assert.Equal(["ERROR"], output);
    }
}
