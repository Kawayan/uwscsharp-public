using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class VariantTypeTests
{
    [Theory]
    [InlineData("1", 5)]
    [InlineData("1.25", 5)]
    [InlineData("TRUE", 11)]
    [InlineData("FALSE", 11)]
    [InlineData("1=1", 11)]
    [InlineData("1<>1", 11)]
    [InlineData("NOTHING=NOTHING", 11)]
    [InlineData("!TRUE", 11)]
    [InlineData("TRUE AND TRUE", 5)]
    [InlineData("1 OR 2", 5)]
    [InlineData("CHKNUM(\"123\")", 11)]
    [InlineData("CHKNUM(\"abc\")", 11)]
    [InlineData("ISUNICODE(\"Aあ\")", 11)]
    [InlineData("ISUNICODE(\"😀\")", 11)]
    [InlineData("\"text\"", 258)]
    [InlineData("EMPTY", 0)]
    [InlineData("NULL", 1)]
    [InlineData("NOTHING", 9)]
    public void QueryPreservesTheObservedExpressionTypes(string source, int expected)
    {
        Value result = VariantTypeRequest.Query(ExpressionEvaluator.Evaluate(source));
        Assert.Equal(expected, result.Number);
        Assert.Equal((ushort?)18, result.NativeVariantType);
        Assert.Equal(18, VariantTypeRequest.Query(result).Number);
    }

    [Fact]
    public void LogicalTypeMetadataDoesNotChangeTheAdoptedNumericRules()
    {
        Value value = ExpressionEvaluator.Evaluate("TRUE");
        Assert.Equal(Value.Numeric(1), value);
        Assert.Equal(Value.Numeric(1).GetHashCode(), value.GetHashCode());
        Assert.Equal("1", value.Display());
        Assert.Equal(1, NumericOperations.RuntimeNumber(value));
        Assert.Equal(1, ExpressionEvaluator.Evaluate("INT(TRUE)").Number);
        Assert.Equal(2, ExpressionEvaluator.Evaluate("TRUE+TRUE").Number);
        Assert.Null(value.GetUnchangedNativeScalar());
        Assert.Equal(5, VariantTypeRequest.Query(value with
        {
            Number = 2
        }).Number);
    }

    [Fact]
    public void QueriesRetainedNativeTypesAndArrayElementTypes()
    {
        Value retained = Value.Numeric(123) with
        {
            NativeVariantType = 3,
            NativeScalar = 123
        };
        Assert.Equal(3, VariantTypeRequest.Query(retained).Number);
        Assert.Equal(5, VariantTypeRequest.Query(retained with
        {
            Number = 124
        }).Number);
        Assert.Equal(8204, VariantTypeRequest.Query(Value.FromArray(UwscArray.Create([1]))).Number);
        Assert.Equal(8209, VariantTypeRequest.Query(Value.FromArray(
            UwscArray.Create([1]).WithNativeElementType(17))).Number);
        Assert.Equal(10, VariantTypeRequest.Query(Value.FromError(7)).Number);
        Assert.Equal(256, VariantTypeRequest.Query(
            new EncodedText([65], 932).ToValue()).Number);
        Value bstr = Value.String("text") with
        {
            NativeVariantType = 8,
            NativeScalar = "text"
        };
        Assert.Equal(258, VariantTypeRequest.Query(bstr).Number);
        Assert.Equal((ushort?)8, bstr.NativeVariantType);
    }

    [Theory]
    [InlineData(0x400c)]
    [InlineData(0x4003)]
    public void ReferenceMetadataDoesNotChangeEqualityAndExpiresAfterNumericMutation(int type)
    {
        Value value = Value.Numeric(123) with
        {
            NativeVariantType = 3,
            NativeScalar = 123,
            NativeReferenceType = (ushort)type
        };
        Assert.Equal(type, VariantTypeRequest.Query(value).Number);
        Assert.Equal(Value.Numeric(123), value);
        Assert.Equal(Value.Numeric(123).GetHashCode(), value.GetHashCode());
        Assert.Equal(18, VariantTypeRequest.Query(VariantTypeRequest.Query(value)).Number);
        Assert.Equal(5, VariantTypeRequest.Query(value with
        {
            Number = 124
        }).Number);
    }

    [Fact]
    public void ReferenceMetadataDoesNotSurviveChangedStringContents()
    {
        Value value = Value.String("A") with
        {
            NativeVariantType = 8,
            NativeScalar = "A",
            NativeReferenceType = 0x4008
        };
        Assert.Equal(0x4008, VariantTypeRequest.Query(value).Number);
        Assert.Equal(258, VariantTypeRequest.Query(value with
        {
            Text = "B"
        }).Number);
    }

    [Fact]
    public void RequestAcceptsAnExistingReportedTypeOutsideTheScalarConversionList()
    {
        Value integer = Value.Numeric(123) with
        {
            NativeVariantType = 3,
            NativeScalar = 123
        };
        var array = Value.FromArray(UwscArray.Create([0], [integer]).WithNativeElementType(3));
        foreach (Value value in new[]
        {
            integer with { NativeReferenceType = 0x4003 },
            integer with { NativeReferenceType = 0x400c },
            Value.Numeric(123) with { NativeVariantType = 14, NativeScalar = 123m },
            Value.Numeric(123) with { NativeVariantType = 22, NativeScalar = 123 },
            Value.Numeric(123) with { NativeVariantType = 23, NativeScalar = 123u },
            array,
            array with { NativeReferenceType = 0x6003 }
        })
        {
            ushort target = (ushort)VariantTypeRequest.Query(value).Number;
            Assert.Equal((ushort?)target,
                VariantTypeRequest.Read([value, Value.Numeric(target)]).Target);
        }
    }

    [Fact]
    public void RequestKeepsOmissionSeparateFromEmptyAndTruncatesTheType()
    {
        Assert.Null(VariantTypeRequest.Read([Value.Numeric(1)]).Target);
        Assert.Null(VariantTypeRequest.Read([Value.Numeric(1), new(ValueKind.Omitted)]).Target);
        Assert.Equal((ushort?)0, VariantTypeRequest.Read([Value.Numeric(1), Value.Empty]).Target);
        Assert.Equal((ushort?)3, VariantTypeRequest.Read([Value.Numeric(1), Value.Numeric(3.9)]).Target);
        Assert.Throws<InvalidOperationException>(() => VariantTypeRequest.Read([]));
        Assert.Throws<InvalidOperationException>(() => VariantTypeRequest.Read([new(ValueKind.Omitted)]));
        Assert.Throws<InvalidOperationException>(() => VariantTypeRequest.Read(
            [Value.Numeric(1), Value.Numeric(8192)]));
        Assert.Throws<InvalidOperationException>(() => VariantTypeRequest.Read(
            [new(ValueKind.Number, double.NaN)]));
        foreach (int type in new[] { 14, 15, 22, 23, 8192 })
        {
            Assert.Throws<InvalidOperationException>(() => VariantTypeRequest.Read(
                [Value.Numeric(123), Value.Numeric(type)]));
        }
    }

    [Theory]
    [InlineData("VARTYPE(Side(),Side(),Side())")]
    [InlineData("VARTYPE(,Side())")]
    [InlineData("VARTYPE()")]
    public async Task InvalidArityIsRejectedBeforeArgumentEffects(string call)
    {
        List<string> output = [];
        await new Interpreter(windows: new Adapter()).RunAsync($$"""
            TRY
                {{call}}
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION Side()
                PRINT "side"
                RESULT=1
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
    }

    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token) => throw new InvalidOperationException("呼び出してはいけません。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
