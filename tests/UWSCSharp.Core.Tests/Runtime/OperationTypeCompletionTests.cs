using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class OperationTypeCompletionTests
{
    [Theory]
    [InlineData("5.9 MOD 2.9", 0.1)]
    [InlineData("-5.9 MOD 2.9", -0.1)]
    [InlineData("5.9 MOD -2.9", 0.1)]
    [InlineData("1 MOD 0.5", 0)]
    [InlineData("-1 MOD 0.5", 0)]
    [InlineData("0 MOD 0.5", 0)]
    [InlineData("\"5.9\" MOD \"2.9\"", 0.1)]
    public void FractionalModuloUsesFiniteDoubleRemainder(
        string expression, double expected)
    {
        Assert.Equal(expected,
            ExpressionEvaluator.Evaluate(expression).AsNumber(), 12);
    }

    [Theory]
    [InlineData("1 MOD 0")]
    [InlineData("1 / 0")]
    [InlineData("1e308 * 1e308")]
    [InlineData("9223372036854775808 AND 1")]
    [InlineData("-9223372036854777856 OR 1")]
    public void InvalidNumericResultsFailBeforePublishingAValue(string expression)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate(expression));
    }

    [Fact]
    public void NativeBooleanKeepsItsTagUntilAnOperationProducesANewNumber()
    {
        Value native = Value.Numeric(1) with
        {
            NativeVariantType = 11,
            NativeScalar = true
        };
        Dictionary<string, Value> variables = new()
        {
            ["native"] = native
        };
        Value copy = ExpressionEvaluator.Evaluate("+native", variables);
        Assert.True(Assert.IsType<bool>(copy.GetUnchangedNativeScalar()));
        Assert.Equal(-1, ExpressionEvaluator.Evaluate("INT(native)", variables)
            .AsNumber());
        Value calculated = ExpressionEvaluator.Evaluate("native+1", variables);
        Assert.Equal(2, calculated.AsNumber());
        Assert.Null(calculated.NativeVariantType);
        Assert.Equal(1, ExpressionEvaluator.Evaluate("TRUE", variables).AsNumber());
    }

    [Fact]
    public void NonNumericKindsHaveExplicitArithmeticAndIdentityBoundaries()
    {
        Value[] values =
        [
            new(ValueKind.Null), Value.Nothing, Value.FromError(7),
            Value.FromObject(new object()),
            Value.FromArray(UwscArray.Create([1])),
            Value.FromHashTable(UwscHashTable.Create(0))
        ];
        foreach (Value value in values)
        {
            Dictionary<string, Value> variables = new()
            {
                ["item"] = value
            };
            foreach (string expression in new[] { "item-1", "item*1",
                "item/1", "item MOD 1", "item AND 1", "-item", "!item" })
            {
                Assert.Throws<InvalidOperationException>(() =>
                    ExpressionEvaluator.Evaluate(expression, variables));
            }

            Assert.Equal(value, ExpressionEvaluator.Evaluate("+item", variables));
        }

        Assert.Equal(1, ExpressionEvaluator.Evaluate("NOTHING=NOTHING")
            .AsNumber());
        Assert.Equal(0, ExpressionEvaluator.Evaluate("NOTHING<>NOTHING")
            .AsNumber());
        Assert.Equal(1, ExpressionEvaluator.Evaluate("EMPTY+1").AsNumber());
    }
}
