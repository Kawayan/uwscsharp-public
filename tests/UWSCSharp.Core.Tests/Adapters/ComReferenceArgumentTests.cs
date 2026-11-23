using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ComReferenceArgumentTests
{
    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal readonly object Root = new();
        internal readonly List<string> Calls = [];
        internal Func<IReadOnlyList<WindowsMemberArgument>, WindowsMemberResult>? Invoke;
        internal Value Property = Value.Numeric(1);
        internal int Disposals;
        internal CancellationTokenSource? CancelOnPropertySet;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            Calls.Add("create");
            return ValueTask.FromResult(Value.FromObject(Root));
        }

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
        {
            Calls.Add(name + (assigned.HasValue ? " set" : " get"));
            if (assigned is Value value)
            {
                Property = value;
                CancelOnPropertySet?.Cancel();
            }

            return ValueTask.FromResult(Property);
        }

        public ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(Value target,
            string name, IReadOnlyList<WindowsMemberArgument> arguments, Value? assigned,
            CancellationToken token)
        {
            Calls.Add(name);
            return ValueTask.FromResult(Invoke!(arguments));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task InputsEvaluateInOrderAndAliasedOutputsWriteInOrder()
    {
        var adapter = new Adapter();
        adapter.Invoke = arguments =>
        {
            Assert.Equal([Value.Numeric(1), Value.Numeric(2), new(ValueKind.Omitted)],
                arguments.Select(argument => argument.Value));
            Assert.Equal([true, true, false], arguments.Select(argument => argument.ByReference));
            return new(Value.Numeric(9), [Value.Numeric(10), Value.Numeric(20), new(ValueKind.Omitted)]);
        };
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC n=0
            DIM o=CREATEOLEOBJ("fixture")
            DIM a[1]=1,2
            PRINT o.Change(VAR a[advance()],VAR a[advance()],)
            PRINT a[0]
            PRINT a[1]
            PRINT n
            FUNCTION advance()
                RESULT=n MOD 2
                n=n+1
            FEND
            """, output.Add);
        Assert.Equal(["9", "10", "20", "4"], output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task DuplicateReferencesUseIndependentInputsAndLaterOutputWins()
    {
        var adapter = new Adapter
        {
            Invoke = arguments =>
            {
                Assert.All(arguments, argument => Assert.Equal(Value.Numeric(1), argument.Value));
                return new(Value.Nothing, [Value.Numeric(2), Value.String("last")]);
            }
        };
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=1
            o.Change(VAR x,VAR x)
            PRINT x
            """, output.Add);
        Assert.Equal(["last"], output);
    }

    [Fact]
    public async Task ArrayHashAndPropertyReferencesAreWritable()
    {
        var adapter = new Adapter
        {
            Invoke = _ => new(Value.Nothing,
                [Value.Numeric(8), Value.String("hash"), Value.Numeric(12)])
        };
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM a[0]=1
            HASHTBL h
            h["key"]=2
            o.Change(VAR a[0],VAR h["key"],VAR o.Value)
            PRINT a[0]
            PRINT h["key"]
            PRINT o.Value
            """, output.Add);
        Assert.Equal(["8", "hash", "12"], output);
        Assert.Equal(["create", "Value get", "Change", "Value set", "Value get"], adapter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationDoesNotPublishOutValues(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            Invoke = _ =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    return new(Value.Nothing, [Value.Numeric(99)]);
                }

                throw new InvalidOperationException("native failure");
            }
        };
        var output = new List<string>();
        Exception? error = await Record.ExceptionAsync(() => new Interpreter(windows: adapter).RunAsync("""
            OPTION OPTFINALLY
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=1
            TRY
                o.Change(VAR x)
            FINALLY
                PRINT x
            ENDTRY
            """, output.Add, cancellation.Token));
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        }
        else
        {
            Assert.Equal("native failure", Assert.IsType<InvalidOperationException>(error).Message);
        }

        Assert.Equal(["1"], output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task LaterCopybackFailurePreservesEarlierWrites()
    {
        var adapter = new Adapter { Invoke = _ => new(Value.Nothing, [Value.Numeric(8), Value.Numeric(9)]) };
        var output = new List<string>();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter).RunAsync("""
            PUBLIC n=0
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=1
            DIM a[0]=2
            TRY
                o.Change(VAR x,VAR a[index()])
            FINALLY
                PRINT x
                PRINT a[0]
            ENDTRY
            FUNCTION index()
                RESULT=n
                n=n+1
            FEND
            """, output.Add));
        Assert.Equal(["8", "2"], output);
    }

    [Fact]
    public async Task CancellationDuringCopybackKeepsCompletedSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter
        {
            CancelOnPropertySet = cancellation,
            Invoke = _ => new(Value.Nothing, [Value.Numeric(8), Value.Numeric(12), Value.Numeric(9)])
        };
        var output = new List<string>();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync("""
                OPTION OPTFINALLY
                DIM o=CREATEOLEOBJ("fixture")
                DIM x=1
                DIM y=2
                TRY
                    o.Change(VAR x,VAR o.Value,VAR y)
                FINALLY
                    PRINT x
                    PRINT y
                    PRINT o.Value
                ENDTRY
                """, output.Add, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["8", "2", "12"], output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task InvalidAdapterOutputCountIsRejectedBeforeAnyWrite()
    {
        var adapter = new Adapter { Invoke = _ => new(Value.Nothing, []) };
        var output = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM x=1
            TRY
                o.Change(VAR x)
            FINALLY
                PRINT x
            ENDTRY
            """, output.Add));
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task ConstantsAreRejectedBeforeCallingMember()
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter).RunAsync("""
            CONST x=1
            DIM o=CREATEOLEOBJ("fixture")
            o.Change(VAR x)
            """, _ => { }));
        Assert.Equal(["create"], adapter.Calls);
    }

    [Theory]
    [InlineData("HASH_KEY")]
    [InlineData("F_READ")]
    [InlineData("missing")]
    public async Task BuiltinConstantsAndUndefinedReferencesDoNotReachMember(string name)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter).RunAsync(
            "DIM o=CREATEOLEOBJ(\"fixture\")\no.Change(VAR " + name + ")", _ => { }));
        Assert.Equal(["create"], adapter.Calls);
    }

    [Theory]
    [InlineData("o.Change(VAR 1)")]
    [InlineData("o.Change(VAR x+1)")]
    [InlineData("o.Change(VAR f())")]
    [InlineData("o.Change(VAR)")]
    public void NonWritableReferencesAreRejectedAtCompilation(string source) =>
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
}
