using System.IO.Pipes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerPipeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalPipeExchangesPreparationStartCancellationAndCompletion(bool cancelEarly)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        using var server = new SchedulerWorkerPipe();
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        var window = new SchedulerWindowTrigger(new(0x20000000000001, 123, 456), observedAt);
        var handshake = new SchedulerWorkerHandshake(identity, Environment.ProcessId);
        Task connected = server.WaitForConnectionAsync(token);
        Task<(bool Started, string[] Arguments)> worker = Task.Run(async () =>
        {
            using NamedPipeClientStream client =
                await SchedulerWorkerPipe.ConnectAsync(server.Name, token);
            Assert.Equal((uint)Environment.ProcessId,
                SchedulerPipeIdentity.Server(client.SafePipeHandle));
            SchedulerWorkerMessage.Request request = Assert.IsType<SchedulerWorkerMessage.Request>(
                await SchedulerWorkerProtocol.ReadAsync(client, identity, token));
            Assert.Equal(window, request.WindowTrigger);
            await SchedulerWorkerProtocol.WriteAsync(client,
                new SchedulerWorkerMessage.Ready(identity, Environment.ProcessId), token);
            SchedulerWorkerMessage command =
                await SchedulerWorkerProtocol.ReadAsync(client, identity, token);
            bool started = command is SchedulerWorkerMessage.Start;
            if (started)
            {
                await SchedulerWorkerProtocol.WriteAsync(client,
                    new SchedulerWorkerMessage.Started(identity), token);
                Assert.IsType<SchedulerWorkerMessage.Cancel>(
                    await SchedulerWorkerProtocol.ReadAsync(client, identity, token));
            }
            else
            {
                Assert.IsType<SchedulerWorkerMessage.Cancel>(command);
            }
            await SchedulerWorkerProtocol.WriteAsync(client,
                new SchedulerWorkerMessage.Completed(identity, 130, true, true, null), token);
            return (started, request.Arguments.ToArray());
        }, token);
        await connected;
        Assert.Equal((uint)Environment.ProcessId,
            SchedulerPipeIdentity.Client(server.Stream.SafePipeHandle));
        string[] expectedArguments = ["", "日本語 \"quote\"", "😀"];
        await SchedulerWorkerProtocol.WriteAsync(server.Stream,
            new SchedulerWorkerMessage.Request(identity, Guid.NewGuid(),
                Path.Combine(Path.GetTempPath(), "対象.uws"), expectedArguments,
                observedAt, window), token);
        if (cancelEarly)
        {
            Assert.Null(handshake.RequestCancellation());
        }
        handshake.AcceptReady(Assert.IsType<SchedulerWorkerMessage.Ready>(
            await SchedulerWorkerProtocol.ReadAsync(server.Stream, identity, token)));
        await SchedulerWorkerProtocol.WriteAsync(server.Stream,
            handshake.CreateNextCommand(), token);
        if (!cancelEarly)
        {
            handshake.AcceptStarted(Assert.IsType<SchedulerWorkerMessage.Started>(
                await SchedulerWorkerProtocol.ReadAsync(server.Stream, identity, token)));
            await SchedulerWorkerProtocol.WriteAsync(server.Stream,
                Assert.IsType<SchedulerWorkerMessage.Cancel>(handshake.RequestCancellation()), token);
        }
        SchedulerWorkerMessage.Completed completed = Assert.IsType<SchedulerWorkerMessage.Completed>(
            await SchedulerWorkerProtocol.ReadAsync(server.Stream, identity, token));
        handshake.AcceptCompletion(completed);
        (bool started, string[] arguments) = await worker;
        Assert.Equal(!cancelEarly, started);
        Assert.Equal(expectedArguments, arguments);
        Assert.Equal(SchedulerWorkerPhase.Completed, handshake.Phase);
        Assert.True(completed.InputRecovered);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("\\\\server\\pipe\\name")]
    [InlineData("UWSCSharp-schedule-../other")]
    public async Task InvalidPipeNamesAreRejectedWithoutConnecting(string name)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerPipe.ConnectAsync(name));
    }

    [Fact]
    public async Task CancelledConnectionDoesNotLeaveTheServerConnected()
    {
        using var server = new SchedulerWorkerPipe();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            server.WaitForConnectionAsync(cancellation.Token));
        Assert.False(server.Stream.IsConnected);
    }
}
