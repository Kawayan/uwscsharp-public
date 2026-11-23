using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class MessageBoxRequestTests
{
    [Fact]
    public void DefaultAndExplicitZeroSelectOkWithoutTurningMissingCoordinatesIntoZero()
    {
        foreach (Value[] arguments in new[]
        {
            new[] { Value.String("確認") },
            [Value.String("確認"), Value.Numeric(0)]
        })
        {
            var request = MessageBoxRequest.Read(arguments);
            Assert.Equal("確認", request.Message);
            Assert.Equal([1], request.VisibleButtons);
            Assert.Equal(1, request.DefaultButton);
            Assert.Null(request.X);
            Assert.Null(request.Y);
            Assert.False(request.Links);
        }
    }

    [Fact]
    public void PublishedButtonOrderAndExplicitFocusAreIndependentOfNumericFlagOrder()
    {
        var request = MessageBoxRequest.Read(
            [Value.String("確認"), Value.Numeric(127)]);
        Assert.Equal([4, 8, 1, 2, 16, 32, 64], request.VisibleButtons);
        Assert.Equal(4, request.DefaultButton);
        request = MessageBoxRequest.Read([Value.String("確認"), Value.Numeric(12),
            new(ValueKind.Omitted), new(ValueKind.Omitted), Value.Numeric(8)]);
        Assert.Equal(8, request.DefaultButton);
        Assert.Null(request.X);
        Assert.Null(request.Y);
    }

    [Fact]
    public void ExplicitCoordinatesIncludingZeroAndNegativeValuesArePreserved()
    {
        var request = MessageBoxRequest.Read([Value.String("確認"),
            Value.Numeric(1), Value.Numeric(0), Value.Numeric(-20),
            new(ValueKind.Omitted), Value.Numeric(1)]);
        Assert.Equal(0, request.X);
        Assert.Equal(-20, request.Y);
        Assert.True(request.Links);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(128, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 3)]
    [InlineData(1, 128)]
    public void UnsupportedButtonOrFocusBitsAreRejected(int buttons, int focus)
        => Assert.Throws<InvalidOperationException>(() => MessageBoxRequest.Read(
            [Value.String("確認"), Value.Numeric(buttons), new(ValueKind.Omitted),
                new(ValueKind.Omitted), Value.Numeric(focus)]));

    [Fact]
    public void MessageAndArityAreValidatedWithoutNativeUi()
    {
        Assert.Throws<InvalidOperationException>(() => MessageBoxRequest.Read([]));
        Assert.Throws<InvalidOperationException>(() => MessageBoxRequest.Read([new(ValueKind.Omitted)]));
        Assert.Throws<InvalidOperationException>(() => MessageBoxRequest.Read(
            [Value.Empty, Value.Empty, Value.Empty, Value.Empty, Value.Empty, Value.Empty, Value.Empty]));
        Assert.Throws<InvalidOperationException>(() => MessageBoxRequest.Read(
            [Value.FromArray(UwscArray.Create([1]))]));
    }

    [Fact]
    public void EveryButtonMaskRetainsOnlyRequestedButtonsAndAnAvailableDefault()
    {
        for (int mask = 1; mask <= 127; mask++)
        {
            var request = MessageBoxRequest.Read(
                [Value.String("確認"), Value.Numeric(mask)]);
            Assert.Equal(mask, request.VisibleButtons.Aggregate(0, (all, bit) => all | bit));
            Assert.Contains(request.DefaultButton, request.VisibleButtons);
            Assert.Equal(request.VisibleButtons.Count(), request.VisibleButtons.Distinct().Count());
        }
    }
    [Fact]
    public void FiniteFractionalCoordinatesAndFlagsTruncateTowardZero()
    {
        var request = MessageBoxRequest.Read([Value.String("確認"), Value.Numeric(1.9),
            Value.Numeric(12.9), Value.Numeric(-4.8), Value.Numeric(1.2)]);
        Assert.Equal(1, request.Buttons);
        Assert.Equal(1, request.DefaultButton);
        Assert.Equal(12, request.X);
        Assert.Equal(-4, request.Y);
    }

}
