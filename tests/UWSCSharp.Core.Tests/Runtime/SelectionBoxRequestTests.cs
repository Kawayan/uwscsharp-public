using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectionBoxRequestTests
{
    [Fact]
    public void ValidationStopsWhenCanceledWhileReadingItems()
    {
        using var cancellation = new CancellationTokenSource();
        var arguments = new CancelingArguments(cancellation);
        OperationCanceledException error =
            Assert.Throws<OperationCanceledException>(() =>
                WindowsFunctions.Validate(
                    BuiltinFunctionNames.SlctBox, arguments,
                    cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed class CancelingArguments(CancellationTokenSource source)
        : IReadOnlyList<Value>
    {
        public int Count => 5;

        public Value this[int index]
        {
            get
            {
                if (index == 3)
                {
                    source.Cancel();
                    return Value.String("A");
                }

                return index switch
                {
                    0 => Value.Numeric(129),
                    1 => Value.Numeric(0),
                    2 => Value.String("message"),
                    _ => throw new InvalidOperationException(
                        "取消後の項目を読み取りました。")
                };
            }
        }

        public IEnumerator<Value> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void PositionalNumbersAndNumericTextHaveDifferentRoles()
    {
        var request = SelectionBoxRequest.Read([Value.Numeric(1), Value.Numeric(0),
            Value.Numeric(80.9), Value.Numeric(-20.5), Value.String("message"), Value.String("A")]);
        Assert.Equal(80, request.X);
        Assert.Equal(-20, request.Y);
        Assert.Equal("message", request.Message);
        Assert.Equal(["A"], request.Items);
        request = SelectionBoxRequest.Read([Value.Numeric(1), Value.Numeric(0),
            Value.String("80"), Value.String("90"), Value.String("A")]);
        Assert.Null(request.X);
        Assert.Equal("80", request.Message);
        Assert.Equal(["90", "A"], request.Items);
    }

    [Theory]
    [InlineData(1, 31)]
    [InlineData(65, 35)]
    [InlineData(129, 35)]
    public void OnlyTheBitResultLimitsItemsTo31(int flags, int expected)
    {
        var items = UwscArray.Create([34], Enumerable.Range(0, 35).Select(index => Value.String("Item" + index)));
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0),
            new(ValueKind.Omitted), Value.FromArray(items)]);
        Assert.Equal("", request.Message);
        Assert.Equal(expected, request.Items.Count);
    }

    [Theory]
    [InlineData(2, ValueKind.Number, "3")]
    [InlineData(66, ValueKind.String, "A\tB")]
    [InlineData(130, ValueKind.String, "0\t1")]
    public void MultipleResultsUseItemOrderAndZeroBasedPositions(int flags, ValueKind kind, string expected)
    {
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0),
            Value.String("message"), Value.String("A"), Value.String("B")]);
        Value result = request.Selected([1, 0, 1]);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(expected, result.Display());
        Assert.Equal(flags == 2 ? "0" : "", request.TimedOut.Display());
        if (kind == ValueKind.Number)
        {
            Assert.Equal((ushort)3, result.NativeVariantType);
            Assert.Equal(3, result.GetUnchangedNativeScalar());
            Assert.Equal((ushort)3, request.TimedOut.NativeVariantType);
            Assert.Equal(0, request.TimedOut.GetUnchangedNativeScalar());
        }
    }

    [Fact]
    public void SingleNumericResultIsAZeroBasedNumberAndNoSelectionIsAnEmptyString()
    {
        var request = SelectionBoxRequest.Read([Value.Numeric(129), Value.Numeric(0),
            Value.String("message"), Value.String("A"), Value.String("B")]);
        Value result = request.Selected([1]);
        Assert.Equal(Value.Numeric(1), result);
        Assert.Equal((ushort)3, result.NativeVariantType);
        Assert.Equal(1, result.GetUnchangedNativeScalar());
        Assert.Equal((ushort)3, SelectionBoxRequest.Closed.NativeVariantType);
        Assert.Equal(-1, SelectionBoxRequest.Closed.GetUnchangedNativeScalar());
        Assert.Equal(Value.String(""), request.Selected([]));
        Assert.Throws<InvalidOperationException>(() => request.Selected([2]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(32)]
    [InlineData(193)]
    [InlineData(-1)]
    public void AmbiguousKindsAndReturnModesAreRejected(int flags) =>
        Assert.Throws<InvalidOperationException>(() => SelectionBoxRequest.Read(
            [Value.Numeric(flags), Value.Numeric(0), Value.String("message")]));

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    public void AnOmittedKindFlagDefaultsToButtons(int flags)
    {
        var request = SelectionBoxRequest.Read([Value.Numeric(flags), Value.Numeric(0), Value.String("message")]);
        Assert.Equal(1, request.Kind);
    }

    [Fact]
    public void TheDeadlineUsesElapsedTimeInsteadOfTimerNotificationCount()
    {
        var clock = new Clock();
        var deadline = new SelectionBoxDeadline(clock, 1.5);
        Assert.Equal(1, deadline.RemainingFraction);
        clock.Tick = 1000;
        Assert.InRange(deadline.RemainingFraction, 0.333, 0.334);
        clock.Tick = 5000;
        Assert.True(deadline.Expired);
        Assert.Equal(0, deadline.RemainingFraction);
        Assert.False(new SelectionBoxDeadline(clock, 0).Expired);
    }

    [Fact]
    public async Task InterpreterKeepsTheMessageHoleAndChecksArityBeforeEffects()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync(
            "PRINT SLCTBOX(SLCT_BTN+SLCT_NUM,0,,\"A\",\"B\")", output.Add);
        Assert.Equal(["1"], output);
        Assert.Equal("", adapter.Request!.Message);
        output.Clear();
        await new Interpreter(windows: adapter).RunAsync("""
            TRY
                SLCTBOX(Probe(),0)
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION Probe()
                PRINT "side"
                RESULT=SLCT_BTN
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
    }


    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal SelectionBoxRequest? Request;

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> values, CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.SlctBox, name);
            Request = SelectionBoxRequest.Read(values);
            return ValueTask.FromResult(Request.Selected([1]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Clock : TimeProvider
    {
        internal long Tick;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Tick;
    }
}
