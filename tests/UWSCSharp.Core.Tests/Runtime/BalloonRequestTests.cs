using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class BalloonRequestTests
{
    [Fact]
    public void EraseAndOmittedAppearanceRemainDistinctFromExplicitValues()
    {
        var erase = BalloonRequest.Read([]);
        Assert.True(erase.Hidden);
        Assert.Equal(erase, BalloonRequest.Read([Value.String("")]));
        Assert.Null(erase.X);
        Assert.Null(erase.Y);
        Assert.Null(erase.Direction);
        Assert.Null(erase.FontSize);
        Assert.Null(erase.FontFamily);
        Assert.Null(erase.TextColor);
        Assert.Null(erase.BackgroundColor);
        Assert.Null(erase.Transparency);
        Assert.Null(erase.Alpha);

        var explicitValues = BalloonRequest.Read([
            Value.String("日本語\r\nsecond"), Value.Numeric(0), Value.Numeric(-20.9),
            Value.Numeric(0), Value.Numeric(16.5), Value.String(" Arial "),
            Value.Numeric(0), Value.Numeric(1), Value.Numeric(0)]);
        Assert.False(explicitValues.Hidden);
        Assert.Equal("日本語\r\nsecond", explicitValues.Message);
        Assert.Equal(0, explicitValues.X);
        Assert.Equal(-20, explicitValues.Y);
        Assert.Equal(0, explicitValues.Direction);
        Assert.Equal(16d, explicitValues.FontSize);
        Assert.Equal("Arial", explicitValues.FontFamily);
        Assert.Equal(0, explicitValues.TextColor);
        Assert.Equal(1, explicitValues.BackgroundColor);
        Assert.Equal(0, explicitValues.Transparency);
        Assert.Equal((byte)255, explicitValues.Alpha);
    }

    [Fact]
    public void IndividualOmissionsAndBgrBitsArePreserved()
    {
        var request = BalloonRequest.Read([
            Value.String("A"), new(ValueKind.Omitted), Value.Numeric(90),
            new(ValueKind.Omitted), new(ValueKind.Omitted),
            new(ValueKind.Omitted), Value.Numeric(0x123456), Value.Numeric(0)]);
        Assert.Null(request.X);
        Assert.Equal(90, request.Y);
        Assert.Null(request.Direction);
        Assert.Null(request.FontSize);
        Assert.Null(request.FontFamily);
        Assert.Equal(0x123456, request.TextColor);
        Assert.Equal(0, request.BackgroundColor);
    }

    [Theory]
    [InlineData(-2, null)]
    [InlineData(-1, null)]
    [InlineData(0, 255)]
    [InlineData(127, 128)]
    [InlineData(255, 0)]
    public void AdoptedOpacityEndpointsAreSeparateFromColorKey(int value, int? alpha)
    {
        var request = BalloonRequest.Read(Arguments(8, Value.Numeric(value)));
        Assert.Equal(value, request.Transparency);
        Assert.Equal(alpha, request.Alpha.HasValue ? (int?)request.Alpha.Value : null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AllDocumentedDirectionsAreAccepted(int direction)
        => Assert.Equal(direction,
            BalloonRequest.Read(Arguments(3, Value.Numeric(direction))).Direction);

    [Theory]
    [InlineData(1, double.NaN)]
    [InlineData(2, double.PositiveInfinity)]
    [InlineData(1, 2147483648d)]
    [InlineData(2, -2147483649d)]
    [InlineData(3, -1)]
    [InlineData(3, 5)]
    [InlineData(4, double.NaN)]
    [InlineData(4, double.PositiveInfinity)]
    [InlineData(4, 134217728)]
    [InlineData(6, -1)]
    [InlineData(6, 16777216)]
    [InlineData(7, -1)]
    [InlineData(7, 16777216)]
    [InlineData(8, -3)]
    [InlineData(8, 256)]
    public void InvalidNumericValuesAreRejected(int index, double value)
        => Assert.Throws<InvalidOperationException>(() =>
            BalloonRequest.Read(Arguments(index, Value.Numeric(value))));

    [Theory]
    [InlineData(0, "A\0B")]
    [InlineData(5, "  ")]
    [InlineData(5, "Arial\0Other")]
    [InlineData(5, "Arial\nOther")]
    public void NativeTextTruncationAndInvalidFontNamesAreRejected(int index, string value)
        => Assert.Throws<InvalidOperationException>(() =>
            BalloonRequest.Read(Arguments(index, Value.String(value))));

    [Theory]
    [InlineData(0d, null)]
    [InlineData(-999d, 6d)]
    [InlineData(-1d, 6d)]
    [InlineData(1d, 6d)]
    [InlineData(5d, 6d)]
    [InlineData(6d, 6d)]
    [InlineData(-0.9, null)]
    [InlineData(0.9, null)]
    [InlineData(5.9, 6d)]
    [InlineData(6.9, 6d)]
    [InlineData(10.75, 10d)]
    public void FontSizeUsesTheDefaultOrTheObservedMinimum(
        double input, double? expected)
    {
        Value[] arguments = Arguments(5, Value.String(""));
        arguments[4] = Value.Numeric(input);
        var request = BalloonRequest.Read(arguments);
        Assert.Equal(expected, request.FontSize);
        Assert.Null(request.FontFamily);
    }

    [Fact]
    public void InvalidEraseArgumentsDoNotBypassValidation()
    {
        Value[] arguments = Arguments(8, Value.Numeric(256));
        arguments[0] = Value.String("");
        Assert.Throws<InvalidOperationException>(() => BalloonRequest.Read(arguments));
        Assert.Throws<InvalidOperationException>(() =>
            BalloonRequest.Read([new(ValueKind.Omitted)]));
        Assert.Throws<InvalidOperationException>(() =>
            BalloonRequest.Read(Arguments(9, Value.Numeric(0))));
    }

    private static Value[] Arguments(int index, Value value)
    {
        Value[] arguments = [.. Enumerable.Repeat(new Value(ValueKind.Omitted), index + 1)];
        arguments[0] = Value.String("message");
        arguments[index] = value;
        return arguments;
    }

    [Fact]
    public async Task ScriptAliasesUseTheSameContractAndRejectArityBeforeEvaluation()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PRINT BALLOON("first",,90)
            PRINT FUKIDASI()
            TRY
                BALLOON("x",0,0,0,16,"Arial",0,0,0,SideEffect())
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION SideEffect()
                PRINT "unexpected"
                RESULT=0
            FEND
            """, output.Add);
        Assert.Equal(["", "", "caught"], output);
        Assert.Equal([BuiltinFunctionNames.Balloon, BuiltinFunctionNames.Fukidasi], adapter.Names);
        Assert.Null(adapter.Requests[0].X);
        Assert.Equal(90, adapter.Requests[0].Y);
        Assert.True(adapter.Requests[1].Hidden);
    }

    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal readonly List<string> Names = [];
        internal readonly List<BalloonRequest> Requests = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Names.Add(name);
            Requests.Add(BalloonRequest.Read(arguments));
            return ValueTask.FromResult(Value.String(""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
