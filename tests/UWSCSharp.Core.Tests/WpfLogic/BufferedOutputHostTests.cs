using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class BufferedOutputHostTests
{
    [Fact]
    public async Task OutputIsBatchedWithSingleFlushRequestInOrder()
    {
        int requests = 0;
        var host = new BufferedOutputHost(() => requests++);
        await host.WriteOutputAsync("A", default);
        await host.WriteOutputAsync("B", default);
        await host.WriteOutputAsync("C", default);
        Assert.Equal(1, requests);
        Assert.Equal(["A", "B", "C"], host.Drain());
        Assert.Empty(host.Drain());
        await host.WriteOutputAsync("D", default);
        Assert.Equal(2, requests);
        Assert.Equal(["D"], host.Drain());
    }

    [Fact]
    public async Task WriterWaitsAtCapacityUntilDrained()
    {
        var host = new BufferedOutputHost(() => { }, capacity: 3);
        await host.WriteOutputAsync("abc", default);
        ValueTask blocked = host.WriteOutputAsync("d", default);
        await Task.Delay(50);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(["abc"], host.Drain());
        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["d"], host.Drain());
    }

    [Fact]
    public async Task OversizedLineIsAcceptedWhenEmpty()
    {
        var host = new BufferedOutputHost(() => { }, capacity: 2);
        await host.WriteOutputAsync("abcdef", default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["abcdef"], host.Drain());
    }

    [Fact]
    public async Task WaitingWriterObservesCancellation()
    {
        var host = new BufferedOutputHost(() => { }, capacity: 1);
        await host.WriteOutputAsync("a", default);
        using var cancellation = new CancellationTokenSource();
        ValueTask blocked = host.WriteOutputAsync("b", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.AsTask());
        Assert.Equal(["a"], host.Drain());
    }

    [Fact]
    public async Task EmptyOutputWaitsAtCapacityUntilDrained()
    {
        int requests = 0;
        var host = new BufferedOutputHost(() => requests++,
            capacity: Environment.NewLine.Length);
        await host.WriteOutputAsync("", default);
        ValueTask blocked = host.WriteOutputAsync("", default);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(1, requests);
        Assert.Equal([""], host.Drain());
        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, requests);
        Assert.Equal([""], host.Drain());
    }

    [Fact]
    public async Task EmptyOutputWaitingForCapacityCanBeCancelled()
    {
        var host = new BufferedOutputHost(() => { },
            capacity: Environment.NewLine.Length);
        await host.WriteOutputAsync("", default);
        using var cancellation = new CancellationTokenSource();
        ValueTask blocked = host.WriteOutputAsync("cancelled", cancellation.Token);
        Assert.False(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blocked.AsTask());
        Assert.Equal([""], host.Drain());
    }

    [Fact]
    public async Task NextOutputWaitsIfItsNewlineWouldExceedCapacity()
    {
        int firstLength = "abc".Length + Environment.NewLine.Length;
        var host = new BufferedOutputHost(() => { }, capacity: firstLength + 1);
        await host.WriteOutputAsync("abc", default);
        ValueTask blocked = host.WriteOutputAsync("x", default);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(["abc"], host.Drain());
        await blocked.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["x"], host.Drain());
    }

    [Fact]
    public async Task ScriptWithEmptyPrintsCanCancelCapacityWaitAndRunAgain()
    {
        var requested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new BufferedOutputHost(() => requested.TrySetResult(),
            capacity: Environment.NewLine.Length);
        var runner = new Interpreter();
        using var cancellation = new CancellationTokenSource();
        Task execution = runner.RunAsync("PRINT\nPRINT", host, cancellation.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal([""], host.Drain());
        await runner.RunAsync("PRINT \"next\"", host);
        Assert.Equal(["next"], host.Drain());
    }

    [Fact]
    public void JoinTruncatesEachLineAndAppendsNewLine()
    {
        string longLine = new('x', OutputText.MaximumLength + 10);
        string joined = OutputText.Join(["a", longLine]);
        Assert.Equal("a" + Environment.NewLine
            + new string('x', OutputText.MaximumLength)
            + Environment.NewLine, joined);
    }

    [Fact]
    public void TrimCountKeepsTailAtLineBoundary()
    {
        Assert.Equal(0, OutputText.TrimCount(
            new string('a', OutputText.MaximumLength)));
        string head = new string('h', 40_000) + "\n";
        string tail = new string('t', 70_000) + "\n";
        Assert.Equal(head.Length, OutputText.TrimCount(head + tail));
    }

    [Fact]
    public void TrimCountCutsInsideSingleLongLine()
    {
        string text = new string('x', OutputText.MaximumLength + 5) + "\n";
        Assert.Equal(text.Length - OutputText.RetainedLength,
            OutputText.TrimCount(text));
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("𠮷")]
    public void JoinDoesNotSplitSurrogatePairAtLimit(string character)
    {
        string prefix = new('a', OutputText.MaximumLength - 1);
        Assert.Equal(prefix + Environment.NewLine,
            OutputText.Join([prefix + character + "tail"]));
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("𠮷")]
    public void TrimDoesNotLeaveLowSurrogateAtStart(string character)
    {
        string prefix = new('a', OutputText.MaximumLength
            - OutputText.RetainedLength + 10);
        string tail = new('b', OutputText.RetainedLength - 1);
        string text = prefix + character + tail;
        int trim = OutputText.TrimCount(text);
        Assert.Equal(prefix.Length + character.Length, trim);
        Assert.Equal(tail, text[trim..]);
    }
}
