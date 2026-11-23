using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerHandshakeTests
{
    private static readonly SchedulerWorkerIdentity Identity =
        new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void StartRequiresReadyFromTheCreatedProcessAndOnlyHappensOnce()
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        Assert.Throws<InvalidOperationException>(() => session.CreateNextCommand());
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptReady(new(Identity, 124)));
        Assert.Equal(SchedulerWorkerPhase.AwaitingReady, session.Phase);
        session.AcceptReady(new(Identity, 123));
        Assert.False(session.HasStarted);
        Assert.IsType<SchedulerWorkerMessage.Start>(session.CreateNextCommand());
        Assert.False(session.HasStarted);
        Assert.Equal(SchedulerWorkerPhase.Starting, session.Phase);
        session.AcceptStarted(new(Identity));
        Assert.True(session.HasStarted);
        Assert.Throws<InvalidOperationException>(() => session.CreateNextCommand());
        Assert.Throws<InvalidDataException>(() => session.AcceptReady(new(Identity, 123)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeStartNeverCreatesStartCommand(bool readyFirst)
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        if (readyFirst)
        {
            session.AcceptReady(new(Identity, 123));
        }
        Assert.Null(session.RequestCancellation());
        if (!readyFirst)
        {
            session.AcceptReady(new(Identity, 123));
        }
        Assert.IsType<SchedulerWorkerMessage.Cancel>(session.CreateNextCommand());
        Assert.False(session.HasStarted);
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(Identity, 0, false, true, null)));
        session.AcceptCompletion(new(Identity, 130, true, true, null));
        Assert.Equal(SchedulerWorkerPhase.Completed, session.Phase);
    }

    [Fact]
    public async Task ConcurrentCancellationOnlyCreatesOneControlMessage()
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        session.AcceptReady(new(Identity, 123));
        session.CreateNextCommand();
        session.AcceptStarted(new(Identity));
        SchedulerWorkerMessage.Cancel?[] messages = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ =>
                Task.Run(() => session.RequestCancellation())));
        Assert.Single(messages, message => message is not null);
        Assert.Equal(SchedulerWorkerPhase.Cancelling, session.Phase);
        Assert.True(session.HasStarted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleNotificationsDoNotAdvanceTheCurrentRun(bool generation)
    {
        var other = new SchedulerWorkerIdentity(
            generation ? Guid.NewGuid() : Identity.Generation,
            generation ? Identity.OccurrenceId : Guid.NewGuid());
        var session = new SchedulerWorkerHandshake(Identity, 123);
        Assert.Throws<InvalidDataException>(() => session.AcceptReady(new(other, 123)));
        session.AcceptReady(new(Identity, 123));
        session.CreateNextCommand();
        session.AcceptStarted(new(Identity));
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(other, 0, false, true, null)));
        Assert.Equal(SchedulerWorkerPhase.Running, session.Phase);
        session.AcceptCompletion(new(Identity, 0, false, true, null));
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(Identity, 0, false, true, null)));
    }

    [Fact]
    public void CompletionAndUnrecoveredInputAreSeparateFacts()
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(Identity, 0, false, true, null)));
        session.AcceptReady(new(Identity, 123));
        session.CreateNextCommand();
        session.AcceptStarted(new(Identity));
        session.AcceptCompletion(new(Identity, 0, false, false, "入力回収待ち"));
        Assert.Equal(SchedulerWorkerPhase.Completed, session.Phase);
        session.Fault();
        Assert.Equal(SchedulerWorkerPhase.Completed, session.Phase);
        Assert.Null(session.RequestCancellation());
    }

    [Fact]
    public void BrokenCommunicationCannotBeReusedOrCompletedByALateMessage()
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        session.AcceptReady(new(Identity, 123));
        session.CreateNextCommand();
        session.AcceptStarted(new(Identity));
        session.Fault();
        Assert.Equal(SchedulerWorkerPhase.Faulted, session.Phase);
        Assert.True(session.HasStarted);
        Assert.Null(session.RequestCancellation());
        Assert.Throws<InvalidOperationException>(() => session.CreateNextCommand());
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(Identity, 0, false, true, null)));
    }

    [Fact]
    public void CancellationBetweenStartCommandAndAcknowledgementKeepsCancelling()
    {
        var session = new SchedulerWorkerHandshake(Identity, 123);
        Assert.Throws<InvalidDataException>(() => session.AcceptStarted(new(Identity)));
        session.AcceptReady(new(Identity, 123));
        session.CreateNextCommand();
        Assert.NotNull(session.RequestCancellation());
        Assert.False(session.HasStarted);
        Assert.Throws<InvalidDataException>(() =>
            session.AcceptCompletion(new(Identity, 130, true, true, null)));
        session.AcceptStarted(new(Identity));
        Assert.True(session.HasStarted);
        Assert.Equal(SchedulerWorkerPhase.Cancelling, session.Phase);
        Assert.Throws<InvalidDataException>(() => session.AcceptStarted(new(Identity)));
        session.AcceptCompletion(new(Identity, 130, true, true, null));
    }
}
