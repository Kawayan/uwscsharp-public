using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerExecutionResultTests
{
    [Theory]
    [InlineData(0, false, true, "Succeeded")]
    [InlineData(0, false, false, "Unknown")]
    [InlineData(1, false, true, "Failed")]
    [InlineData(1, false, false, "Failed")]
    [InlineData(130, false, true, "Failed")]
    [InlineData(130, true, true, "Cancelled")]
    [InlineData(130, true, false, "Cancelled")]
    public void CompletionUsesAcknowledgementAndRecovery(int code,
        bool cancellation, bool recovered, string expected)
    {
        var result = SchedulerExecutionResult.Completed(
            code, true, cancellation, recovered, "diagnostic");
        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(code, result.ExitCode);
        Assert.Equal(recovered, result.ResourcesRecovered);
        Assert.True(result.Started);
        Assert.False(result.Forced);
        Assert.Equal("diagnostic", result.Diagnostic);
    }

    [Fact]
    public void UnknownRetainsKnownCodeWithoutInventingSuccess()
    {
        var result = SchedulerExecutionResult.Unknown(
            true, 0, true, "lost completion", forced: true);
        Assert.Equal(SchedulerExecutionOutcome.Unknown, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ResourcesRecovered);
        Assert.True(result.Forced);
    }

    [Fact]
    public void ConfirmedStartFailureDoesNotInventAnExitCode()
    {
        var result = SchedulerExecutionResult.FailedToStart("missing executable");
        Assert.Equal(SchedulerExecutionOutcome.Failed, result.Outcome);
        Assert.False(result.Started);
        Assert.Null(result.ExitCode);
        Assert.True(result.ResourcesRecovered);
        Assert.Throws<ArgumentException>(() => SchedulerExecutionResult.Completed(
            0, false, true, true));
    }

    [Fact]
    public void MultipleUnrecoveredReservationsRequireIndividualRecovery()
    {
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        SchedulerExecutionGate.Reservation first = Assert.IsType<
            SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        first.Bind(SchedulerExecutionMode.SeparateProcess);
        SchedulerExecutionGate.Reservation second = Assert.IsType<
            SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        second.Bind(SchedulerExecutionMode.SeparateProcess);
        first.MarkUnrecovered();
        first.MarkUnrecovered();
        second.MarkUnrecovered();
        gate.SetInputCleanupConfirmed(true);
        first.ReleaseAfterCleanup();
        first.ReleaseAfterCleanup();
        Assert.False(gate.CanEnterInteractive);
        Assert.Null(gate.TryBeginDispatch());
        second.ReleaseAfterCleanup();
        Assert.True(gate.CanEnterInteractive);
        Assert.Equal(0, gate.ScheduledCount);
        Assert.Throws<ObjectDisposedException>(second.MarkUnrecovered);
    }
}
