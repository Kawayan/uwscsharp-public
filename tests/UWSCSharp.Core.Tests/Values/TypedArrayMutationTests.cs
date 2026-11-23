using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class TypedArrayMutationTests
{
    private sealed class Automation(UwscArray array) : IWindowsAutomation,
        IWindowsAutomationSession
    {
        private readonly object root = new();

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.CreateOleObj, name);
            return ValueTask.FromResult(Value.FromObject(root));
        }

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned,
            CancellationToken cancellationToken)
        {
            Assert.Same(root, target.ObjectReference);
            Assert.Equal("Values", name);
            return ValueTask.FromResult(Value.FromArray(array));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static UwscArray Create(int[] lower, int[] upper,
        Func<Value, CancellationToken, Value>? convert = null) =>
        UwscArray.CreateBounded(lower, upper,
            Enumerable.Repeat(Value.Numeric(0), UwscArray.ValidateBounds(lower, upper)))
            .WithNativeElementType(3)
            .WithElementConversion(Value.Numeric(0), convert ?? Convert);

    private static Value Convert(Value value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Value.Numeric(NumericOperations.Integer(
            Value.Numeric(NumericOperations.RuntimeNumber(value)))) with
        {
            NativeVariantType = 3
        };
    }

    [Fact]
    public void ScalarUpdatesAndRowsKeepTheirConversionAndBounds()
    {
        UwscArray original = Create([-2, 5], [-1, 6]);
        UwscArray updated = original.Set([-2, 5], Value.String("7"));
        Assert.Equal(Value.Numeric(0), original.Get(-2, 5));
        Assert.Equal(ValueKind.Number, updated.Get(-2, 5).Kind);
        Assert.Equal(7, updated.Get(-2, 5).Number);
        Assert.Equal((ushort)3, updated.Get(-2, 5).NativeVariantType);
        UwscArray row = updated.Get(-2).AsArray().Set([6], Value.String("8"));
        Assert.Equal([5], row.LowerBounds);
        Assert.Equal([6], row.UpperBounds);
        Assert.Equal((ushort)3, row.NativeElementType);
        Assert.Equal(8, row.Get(6).Number);
        UwscArray combined = updated.Set([-1], Value.FromArray(row));
        Assert.Equal(7, combined.Get(-1, 5).Number);
        Assert.Equal(8, combined.Get(-1, 6).Number);
        Assert.Equal(0, updated.Get(-1, 6).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedBulkConversionKeepsOriginalValues(bool partialRow)
    {
        UwscArray original = Create([-2, 5], [-1, 6]);
        Value[] values = [Value.String("7"), Value.String("bad")];
        Assert.Throws<InvalidOperationException>(() => partialRow
            ? original.Set([-2], Value.FromArray(UwscArray.CreateBounded([5], [6], values)))
            : original.WithValues(values));
        Assert.All(original.Values, value => Assert.Equal(0, value.Number));
    }

    [Fact]
    public void WrongShapeDoesNotCallTheConverter()
    {
        int calls = 0;
        UwscArray original = Create([-2, 5], [-1, 6], (value, token) =>
        {
            calls++;
            return Convert(value, token);
        });
        Assert.Throws<InvalidOperationException>(() => original.Set([-2],
            Value.FromArray(UwscArray.CreateBounded([0], [1]))));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void BulkUpdateAndResizeKeepTypedDefaultsAndFutureConversion()
    {
        UwscArray original = Create([-2], [-1]);
        UwscArray replaced = original.WithValues([Value.String("7")]);
        Assert.Equal(7, replaced.Get(-2).Number);
        Assert.Equal(ValueKind.Number, replaced.Get(-1).Kind);
        Assert.Equal(0, replaced.Get(-1).Number);
        UwscArray resized = replaced.Resize(1);
        Assert.Equal(7, resized.Get(-2).Number);
        Assert.Equal(ValueKind.Number, resized.Get(1).Kind);
        Assert.Equal(0, resized.Get(1).Number);
        Assert.Equal(9, resized.Set([1], Value.String("9")).Get(1).Number);
        Assert.Equal([-2], resized.LowerBounds);
        Assert.Equal((ushort)3, resized.NativeElementType);
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("shift")]
    [InlineData("sort")]
    public void ArrayOperationsKeepTheConversion(string operation)
    {
        UwscArray source = Create([0], [1]).WithValues(
            [Value.String("2"), Value.String("1")]);
        UwscArray updated = operation switch
        {
            "fill" => ArrayOperations.Fill(source, Value.String("7")),
            "shift" => ArrayOperations.Shift(source, 1),
            _ => ArrayOperations.Sort([source], 0, CancellationToken.None)[0]
        };
        Assert.All(updated.Values, value => Assert.Equal(ValueKind.Number, value.Kind));
        Assert.Equal((ushort)3, updated.NativeElementType);
        Assert.Equal(8, updated.Set([0], Value.String("8")).Get(0).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringConversionKeepsOriginalValues(bool partialRow)
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        UwscArray original = Create([0, 0], [0, 1], (value, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            cancellation.Cancel();
            return Value.Numeric(7);
        });
        Assert.ThrowsAny<OperationCanceledException>(() => partialRow
            ? original.Set([0], Value.FromArray(UwscArray.CreateBounded([0], [1],
                [Value.String("7"), Value.String("8")])), cancellation.Token)
            : original.WithValues([Value.String("7"), Value.String("8")], cancellation.Token));
        Assert.Equal(1, calls);
        Assert.All(original.Values, value => Assert.Equal(0, value.Number));
    }

    [Fact]
    public void ArraysWithoutAConversionKeepExistingValueKinds()
    {
        var source = UwscArray.CreateBounded([-2], [-1]);
        Assert.Equal(ValueKind.String, source.Set([-2], Value.String("7")).Get(-2).Kind);
        Assert.Equal(ValueKind.String, source.WithValues([Value.String("7")]).Get(-2).Kind);
        Assert.Equal(ValueKind.Empty, source.Resize(0).Get(0).Kind);
        Assert.Throws<InvalidOperationException>(() =>
            source.WithElementConversion(Value.Numeric(0), Convert));
    }

    [Theory]
    [InlineData("a[0]=\"7\"", 1)]
    [InlineData("SETCLEAR(a,\"7\")", 2)]
    [InlineData("SHIFTARRAY(a,1)", 2)]
    [InlineData("QSORT(a)", 2)]
    [InlineData("F(a[0])\nPROCEDURE F(VAR x)\nx=\"7\"\nFEND", 1)]
    public async Task InterpreterPassesItsTokenToTypedUpdates(string statement,
        int expectedCalls)
    {
        int calls = 0;
        UwscArray source = Create([0], [1], (value, token) =>
        {
            Assert.True(token.CanBeCanceled);
            calls++;
            return Convert(value, token);
        });
        using var cancellation = new CancellationTokenSource();
        await new Interpreter(windows: new Automation(source)).RunAsync(
            "a=CREATEOLEOBJ(\"array\").Values\n" + statement, _ => { }, cancellation.Token);
        Assert.Equal(expectedCalls, calls);
        Assert.All(source.Values, value => Assert.Equal(0, value.Number));
    }

    [Fact]
    public async Task InterpreterCancellationDoesNotPublishAPartialBulkUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        UwscArray source = Create([0], [1], (value, token) =>
        {
            calls++;
            cancellation.Cancel();
            return Value.Numeric(7);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: new Automation(source)).RunAsync(
                "a=CREATEOLEOBJ(\"array\").Values\nSETCLEAR(a,\"7\")", _ => { }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.All(source.Values, value => Assert.Equal(0, value.Number));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleAssignmentCancellationDoesNotPublishConvertedValues(bool row)
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        bool receivedCancellation = false;
        UwscArray source = Create(row ? [0, 0] : [0], row ? [0, 1] : [1],
            (value, token) =>
            {
                calls++;
                receivedCancellation = token.CanBeCanceled;
                cancellation.Cancel();
                return Value.Numeric(7);
            });
        var output = new List<string>();
        string assignment = row ? "M.a[0]=replacement" : "M.a[0]=7";
        string read = row ? "M.a[0,0]" : "M.a[0]";
        string script = "OPTION OPTFINALLY\n"
            + "DIM replacement[1]=7,8\nTRY\n" + assignment
            + "\nFINALLY\nPRINT " + read + "\nENDTRY\n"
            + "MODULE M\nPUBLIC a=CREATEOLEOBJ(\"array\").Values\nENDMODULE\n";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: new Automation(source)).RunAsync(
                script, output.Add, cancellation.Token));
        Assert.True(receivedCancellation);
        Assert.Equal(1, calls);
        Assert.Equal(["0"], output);
        Assert.All(source.Values, value => Assert.Equal(0, value.Number));
    }
}
