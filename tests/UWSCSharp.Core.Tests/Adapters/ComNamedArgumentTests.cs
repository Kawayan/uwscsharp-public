using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ComNamedArgumentTests
{
    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        private readonly object root = new();
        internal Func<IReadOnlyList<WindowsMemberArgument>, Value?, WindowsMemberResult>? Operation;
        internal int Calls;
        internal int Disposals;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token) => ValueTask.FromResult(Value.FromObject(root));

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(Value target,
            string name, IReadOnlyList<WindowsMemberArgument> arguments, Value? assigned,
            CancellationToken token)
        {
            Assert.Same(root, target.AsObject());
            Calls++;
            return ValueTask.FromResult(Operation!(arguments, assigned));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task NamedPositionalVarOmittedAndComparisonEvaluateInSourceOrder()
    {
        var adapter = new Adapter
        {
            Operation = (arguments, assigned) =>
            {
                Assert.Null(assigned);
                Assert.Equal(["B", "A", null, "Empty", "EQ"], arguments.Select(argument => argument.Name));
                Assert.Equal([false, true, false, false, false], arguments.Select(argument => argument.ByReference));
                Assert.Equal([Value.Numeric(1), Value.Numeric(7), Value.Numeric(2),
                    new(ValueKind.Omitted), Value.Numeric(1)], arguments.Select(argument => argument.Value));
                return new(Value.Numeric(99), [Value.Numeric(1), Value.Numeric(20), Value.Numeric(2),
                    new(ValueKind.Omitted), Value.Numeric(1)]);
            }
        };
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC n=0
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=7
            PRINT o.Change(B=advance(),VAR A:=x,advance(),Empty=,EQ=(x=7))
            PRINT x
            PRINT n
            FUNCTION advance()
                n=n+1
                RESULT=n
            FEND
            """, output.Add);
        Assert.Equal(["99", "20", "2"], output);
    }

    [Fact]
    public async Task NamedVarAliasesWriteBackInSourceOrder()
    {
        var adapter = new Adapter
        {
            Operation = (arguments, _) =>
            {
                Assert.Equal(["Z", null, "A"], arguments.Select(argument => argument.Name));
                return new(Value.Nothing, [Value.Numeric(10), Value.Numeric(3), Value.Numeric(20)]);
            }
        };
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=1
            o.Change(Z=VAR x,3,VAR A=x)
            PRINT x
            """, output.Add);
        Assert.Equal(["20"], output);
    }

    [Fact]
    public async Task NamedSetterWithoutVarUsesMetadataEntry()
    {
        var adapter = new Adapter
        {
            Operation = (arguments, assigned) =>
            {
                Assert.Equal("Index", Assert.Single(arguments).Name);
                Assert.Equal(Value.Numeric(7), assigned);
                return new(Value.Nothing, [Value.Numeric(1)]);
            }
        };
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            o.Change(Index=1)=7
            """, _ => { });
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("o.Change(A=1,a=2)")]
    [InlineData("o.Change(A:1)")]
    [InlineData("o.Change(A:=VAR 1)")]
    [InlineData("o.Change(VAR A=)")]
    public void InvalidNamedSyntaxIsRejectedBeforeExecution(string source) =>
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));

}
