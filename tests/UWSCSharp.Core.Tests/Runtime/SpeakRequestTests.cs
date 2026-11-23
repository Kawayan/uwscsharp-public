using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeakRequestTests
{
    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(1, 0, true, false)]
    [InlineData(0, 1, false, true)]
    [InlineData(1, 1, true, true)]
    [InlineData(0.5, 0.5, false, true)]
    [InlineData(-1.5, -0.5, true, true)]
    public void ReadsDistinctNumericFlags(double parallel, double interrupt,
        bool expectedParallel, bool expectedInterrupt)
    {
        var request = SpeakRequest.Read([Value.String("読み上げ"),
            Value.Numeric(parallel), Value.Numeric(interrupt)]);

        Assert.Equal("読み上げ", request.Text);
        Assert.Equal(expectedParallel, request.Parallel);
        Assert.Equal(expectedInterrupt, request.Interrupt);
    }

    [Fact]
    public void DistinguishesAbsentTextFromEmptyTextAndDefaultsFlags()
    {
        Assert.Null(SpeakRequest.Read([]).Text);
        Assert.Equal(new SpeakRequest("", false, false),
            SpeakRequest.Read([Value.String("")]));
        Assert.Equal(new SpeakRequest("1", false, true), SpeakRequest.Read(
            [Value.Numeric(1), new(ValueKind.Omitted), Value.String("0.5")]));
    }

    [Fact]
    public void RejectsMissingTextAndInvalidFlagsOrArity()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SpeakRequest.Read([new(ValueKind.Omitted), Value.Numeric(1)]));
        Assert.Throws<InvalidOperationException>(() =>
            SpeakRequest.Read([Value.String("text"), Value.String("invalid")]));
        Assert.Throws<InvalidOperationException>(() =>
            SpeakRequest.Read([Value.String("text"), Value.Numeric(0),
                Value.Numeric(0), Value.Numeric(0)]));
    }
}
