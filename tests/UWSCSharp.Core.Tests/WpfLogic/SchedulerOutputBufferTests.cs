using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerOutputBufferTests
{
    [Fact]
    public void OversizedLineRetainsTailWithoutSplittingSurrogatePair()
    {
        var buffer = new SchedulerOutputBuffer();
        int capacity = SchedulerOutputBuffer.MaximumLength - Environment.NewLine.Length;
        string line = "😀" + new string('a', capacity - 1);
        buffer.Append(line);
        Assert.Equal(new string('a', capacity - 1) + Environment.NewLine,
            buffer.Render());
        Assert.Equal(2, buffer.OmittedCodeUnits);
        Assert.True(buffer.Render().Length <= SchedulerOutputBuffer.MaximumLength);
    }

    [Fact]
    public void MaximumLinesRemovesOldOutputAndCountsOmittedCodeUnits()
    {
        var buffer = new SchedulerOutputBuffer();
        buffer.Append("first");
        buffer.Append("second");
        buffer.SetMaximumLines(1);
        Assert.Equal("second" + Environment.NewLine, buffer.Render());
        Assert.Equal(5 + Environment.NewLine.Length, buffer.OmittedCodeUnits);
        buffer.SetMaximumLines(0);
        buffer.Append("hidden");
        Assert.Equal("", buffer.Render());
        Assert.Equal(17 + 3 * Environment.NewLine.Length, buffer.OmittedCodeUnits);
        buffer.SetMaximumLines(1);
        buffer.Append("new");
        Assert.Equal("new" + Environment.NewLine, buffer.Render());
    }

    [Fact]
    public async Task ConcurrentWritersStayWithinCapacityAndRemainIndependent()
    {
        var first = new SchedulerOutputBuffer();
        var second = new SchedulerOutputBuffer();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            Task.Run(() => first.Append(new string('x', 1000)))));
        second.Append("second");
        Assert.True(first.Render().Length <= SchedulerOutputBuffer.MaximumLength);
        Assert.True(first.OmittedCodeUnits > 0);
        Assert.Equal("second" + Environment.NewLine, second.Render());
        Assert.Equal(0, second.OmittedCodeUnits);
    }
}
