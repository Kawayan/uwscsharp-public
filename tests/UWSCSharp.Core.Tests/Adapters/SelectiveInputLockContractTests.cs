using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputLockContractTests
{
    [Theory]
    [InlineData("LOCKHARDEX()", 0)]
    [InlineData("lockhardex(0)", 1)]
    [InlineData("LOCKHARDEX(1,)", 2)]
    [InlineData("LOCKHARDEX(1,LOCK_KEYBOARD)", 2)]
    [InlineData("LOCKHARDEX(1,LOCK_MOUSE)", 2)]
    [InlineData("LOCKHARDEX(\"1\",\"2\")", 2)]
    public async Task PublicCallsReachTheAdapterAndReturnEmpty(string call, int count)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($"PRINT {call}=EMPTY", output.Add);
        Assert.Equal(["1"], output);
        Value[] arguments = Assert.Single(adapter.Calls);
        Assert.Equal(count, arguments.Length);
        Assert.True(adapter.Disposed);
    }

    [Theory]
    [InlineData("LOCKHARDEX(,Tick())")]
    [InlineData("LOCKHARDEX(Tick(),Tick(),Tick())")]
    public async Task InvalidShapeRejectsArgumentsBeforeSideEffects(string call)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($"""
            PUBLIC calls=0
            TRY
                {call}
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick()
                calls=calls+1
                RESULT=0
            FEND
            """, output.Add);
        Assert.Equal(["0"], output);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task AcceptedExpressionsAreEvaluatedOnceInOrderBeforeValueValidation()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC calls=""
            TRY
                LOCKHARDEX(Tick(1),Tick(3))
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick(value)
                calls=calls+value
                RESULT=value
            FEND
            """, output.Add);
        Assert.Equal(["13"], output);
        Assert.Empty(adapter.Calls);
    }

    [Theory]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("2147483648")]
    [InlineData("0,EMPTY")]
    [InlineData("0,3")]
    [InlineData("0,-1")]
    [InlineData("0,0.5")]
    public async Task InvalidValuesCannotReachTheAdapter(string arguments)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter)
            .RunAsync($"LOCKHARDEX({arguments})", _ => { }));
        Assert.Empty(adapter.Calls);
    }

    [Theory]
    [InlineData("LOCK_KEYBOARD", 1)]
    [InlineData("lock_mouse", 2)]
    public void ModeConstantsAreNumericAndCannotBeRedefined(string name, int number)
    {
        Assert.Equal(Value.Numeric(number), ExpressionEvaluator.Evaluate(name));
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile($"CONST {name}=7"));
    }


    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<Value[]> Calls { get; } = [];
        internal bool Disposed;
        public IWindowsAutomationSession CreateSession() => this;
        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.LockHardEx, name);
            Calls.Add([.. arguments]);
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
