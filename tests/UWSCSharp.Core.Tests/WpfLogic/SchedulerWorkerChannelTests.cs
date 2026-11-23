using System.Text;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerChannelTests
{
    [Fact]
    public async Task ConcurrentSendersDoNotInterleaveFrames()
    {
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var stream = new MemoryStream();
        using var channel = new SchedulerWorkerChannel(stream, identity);
        await Task.WhenAll(Enumerable.Range(1, 100).Select(id =>
            channel.SendAsync(new SchedulerWorkerMessage.Ready(identity, id))));
        stream.Position = 0;
        var received = new HashSet<int>();
        for (int index = 0; index < 100; index++)
        {
            SchedulerWorkerMessage.Ready ready = Assert.IsType<SchedulerWorkerMessage.Ready>(
                await channel.ReadAsync());
            Assert.True(received.Add(ready.ProcessId));
        }
        Assert.Equal(100, received.Count);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task CancelledAndForeignWritesLeaveTheChannelUsable()
    {
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var stream = new MemoryStream();
        using var channel = new SchedulerWorkerChannel(stream, identity);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.SendAsync(new SchedulerWorkerMessage.Start(identity), cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.SendAsync(
            new SchedulerWorkerMessage.Start(new(Guid.NewGuid(), identity.OccurrenceId))));
        Assert.Equal(0, stream.Length);
        await channel.SendAsync(new SchedulerWorkerMessage.Start(identity));
        stream.Position = 0;
        Assert.IsType<SchedulerWorkerMessage.Start>(await channel.ReadAsync());
    }

    [Fact]
    public async Task DiagnosticIsValidAndFitsControlFrameEvenWithEscapedCharacters()
    {
        string input = new string('\u0001', 4095) + "😀" + new string('a', 20000);
        string text = Assert.IsType<string>(SchedulerWorkerDiagnostic.Limit(input));
        Assert.Contains("省略", text);
        _ = new UTF8Encoding(false, true).GetBytes(text);
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var stream = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(stream,
            new SchedulerWorkerMessage.Completed(identity, 1, false, true, text));
        Assert.True(stream.Length <= SchedulerWorkerProtocol.MaximumControlBytes + 5);
        string invalid = "before\0" + '\ud800' + "after";
        string normalized = Assert.IsType<string>(SchedulerWorkerDiagnostic.Limit(invalid));
        Assert.DoesNotContain('\0', normalized);
        Assert.Contains("\\0", normalized);
        _ = new UTF8Encoding(false, true).GetBytes(normalized);
        Assert.Null(SchedulerWorkerDiagnostic.Limit(null));
    }
}
