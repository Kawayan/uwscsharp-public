using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ComExpressionTests
{
    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal readonly object Root = new();
        internal readonly List<string> Calls = [];
        internal int Disposals;
        internal Value Assigned;
        internal IReadOnlyList<Value> LastArguments = [];
        internal CancellationTokenSource? CancelOnMember;

        public IWindowsAutomationSession CreateSession() => this;

        public IReadOnlyList<Value> AllOleItems => [Value.String("first"), Value.String("second")];

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add(name);
            return ValueTask.FromResult(name == "GETOLEITEM"
                ? Value.Numeric(2) : Value.FromObject(Root));
        }

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken cancellationToken)
        {
            Assert.Same(Root, target.AsObject());
            Calls.Add(name);
            CancelOnMember?.Cancel();
            LastArguments = arguments;
            if (assigned is Value value)
            {
                Assigned = value;
            }

            return ValueTask.FromResult(name switch
            {
                "Child" => Value.FromObject(Root),
                "Number" => Value.Numeric(6),
                _ => new Value(ValueKind.Empty)
            });
        }

        public ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(Value target,
            CancellationToken cancellationToken)
        {
            Calls.Add("enumerate");
            return ValueTask.FromResult(AllOleItems);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task CancellationFromMemberAdapterPreventsOutputAndReleasesSession()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new Adapter { CancelOnMember = cancellation };
        var output = new List<string>();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                PRINT o.Number
                """, output.Add, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(output);
        Assert.Equal(["CREATEOLEOBJ", "Number"], adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task ChainedIndexedPropertyAssignmentEvaluatesInSourceOrder()
    {
        var adapter = new Adapter();
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            o.Child.Item(o.Number)=o.Number+2
            PRINT -o.Number*2
            PRINT (o.Child).Number
            PRINT CREATEOLEOBJ("fixture").Number
            o.Method(,o.Number,)
            """, output.Add);
        Assert.Equal(Value.Numeric(8), adapter.Assigned);
        Assert.Equal(["-12", "6", "6"], output);
        Assert.Equal(["CREATEOLEOBJ", "Child", "Number", "Number", "Item",
            "Number", "Child", "Number", "CREATEOLEOBJ", "Number", "Number", "Method"], adapter.Calls);
        Assert.Equal([new(ValueKind.Omitted), Value.Numeric(6), new(ValueKind.Omitted)], adapter.LastArguments);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task CollectionConsumersUseSnapshotsAndPublishAllOleItems()
    {
        var adapter = new Adapter();
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync("""
            DIM o=GETACTIVEOLEOBJ("fixture",,)
            PRINT LENGTH(o)
            PRINT GETOLEITEM(o)
            PRINT ALL_OLE_ITEM[1]
            FOR item IN o
                PRINT item
            NEXT
            FOR item IN GETOLEITEM(o)
                PRINT item
            NEXT
            """, output.Add);
        Assert.Equal(["2", "2", "second", "first", "second", "first", "second"], output);
        Assert.Equal(["GETACTIVEOLEOBJ", "enumerate", "GETOLEITEM", "enumerate", "GETOLEITEM"], adapter.Calls);
    }

    [Fact]
    public async Task DecimalArrayIndicesAndNumericLiteralsStillParse()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            DIM a[2.0]
            a[1.0]=3.5
            PRINT a[1]
            PRINT .5+1.25
            """, output.Add);
        Assert.Equal(["3.5", "1.75"], output);
    }


    [Theory]
    [InlineData("CREATEOLEOBJ(\"\")")]
    [InlineData("CREATEOLEOBJ(1)")]
    [InlineData("GETACTIVEOLEOBJ(\"fixture\",\"title\")")]
    [InlineData("GETACTIVEOLEOBJ(\"fixture\",\"\")")]
    [InlineData("GETACTIVEOLEOBJ(\"fixture\",,1)")]
    [InlineData("GETACTIVEOLEOBJ(\"fixture\",,0)")]
    [InlineData("GETOLEITEM(NOTHING)")]
    public async Task InvalidComArgumentsDoNotReachAdapter(string expression)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter).RunAsync(
            "PRINT " + expression, _ => { }));
        Assert.Empty(adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("DIM o.Number=3")]
    [InlineData("o.()")]
    [InlineData("o.Method(VAR 1)")]
    [InlineData("o.Method(x=1,X=2)")]
    public void UnsupportedMemberSyntaxIsRejected(string source) =>
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
}
