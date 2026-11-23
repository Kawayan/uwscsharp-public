using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerExecutionGateTests
{
    [Fact]
    public void UnknownCleanupBlocksAllNewAdmissions()
    {
        var gate = new SchedulerExecutionGate();
        Assert.Null(gate.TryBeginDispatch());
        Assert.Null(gate.TryEnterInteractive());
        gate.SetInputCleanupConfirmed(true);
        SchedulerExecutionGate.Reservation reservation =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        gate.SetInputCleanupConfirmed(false);
        Assert.Throws<InvalidOperationException>(() =>
            reservation.Bind(SchedulerExecutionMode.SeparateProcess));
        Assert.Null(gate.TryBeginDispatch());
        reservation.ReleaseAfterCleanup();
        Assert.Equal(0, gate.ScheduledCount);
    }

    [Fact]
    public void FourReservationsHoldCapacityUntilExplicitCleanup()
    {
        SchedulerExecutionGate gate = Ready();
        var reservations = new List<SchedulerExecutionGate.Reservation>();
        for (int index = 0; index < 4; index++)
        {
            SchedulerExecutionGate.Reservation reservation =
                Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
            reservation.Bind(SchedulerExecutionMode.SeparateProcess);
            reservations.Add(reservation);
        }

        Assert.Equal(4, gate.ScheduledCount);
        Assert.Null(gate.TryBeginDispatch());
        reservations[0].ReleaseAfterCleanup();
        SchedulerExecutionGate.Reservation replacement =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        reservations[0].ReleaseAfterCleanup();
        Assert.Equal(4, gate.ScheduledCount);
        replacement.ReleaseAfterCleanup();
        foreach (SchedulerExecutionGate.Reservation reservation in reservations)
        {
            reservation.ReleaseAfterCleanup();
        }

        Assert.Equal(0, gate.ScheduledCount);
    }

    [Fact]
    public void InteractiveBlocksSameProcessButAllowsSeparateProcess()
    {
        SchedulerExecutionGate gate = Ready();
        using IDisposable activity = Assert.IsAssignableFrom<IDisposable>(
            gate.TryEnterInteractive());
        Assert.Null(gate.TryEnterInteractive());
        SchedulerExecutionGate.Reservation reservation =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        Assert.False(reservation.AllowsSameProcess);
        Assert.Throws<InvalidOperationException>(() =>
            reservation.Bind(SchedulerExecutionMode.SameProcess));
        reservation.Bind(SchedulerExecutionMode.SeparateProcess);
        reservation.ReleaseAfterCleanup();
    }

    [Fact]
    public void TentativeSameProcessReservationClosesEditorRace()
    {
        SchedulerExecutionGate gate = Ready();
        SchedulerExecutionGate.Reservation reservation =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        Assert.True(reservation.AllowsSameProcess);
        Assert.Null(gate.TryEnterInteractive());
        reservation.Bind(SchedulerExecutionMode.SeparateProcess);
        using IDisposable activity = Assert.IsAssignableFrom<IDisposable>(
            gate.TryEnterInteractive());
        Assert.Throws<InvalidOperationException>(() =>
            reservation.Bind(SchedulerExecutionMode.SeparateProcess));
        reservation.ReleaseAfterCleanup();
    }

    [Fact]
    public void SameProcessStaysExclusiveAndOldReleaseCannotClearNewOwner()
    {
        SchedulerExecutionGate gate = Ready();
        SchedulerExecutionGate.Reservation first =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        first.Bind(SchedulerExecutionMode.SameProcess);
        Assert.Null(gate.TryEnterInteractive());
        SchedulerExecutionGate.Reservation second =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        Assert.False(second.AllowsSameProcess);
        second.Bind(SchedulerExecutionMode.SeparateProcess);
        first.ReleaseAfterCleanup();
        SchedulerExecutionGate.Reservation third =
            Assert.IsType<SchedulerExecutionGate.Reservation>(gate.TryBeginDispatch());
        third.Bind(SchedulerExecutionMode.SameProcess);
        first.ReleaseAfterCleanup();
        Assert.Null(gate.TryEnterInteractive());
        Assert.Throws<ObjectDisposedException>(() =>
            first.Bind(SchedulerExecutionMode.SeparateProcess));
        third.ReleaseAfterCleanup();
        second.ReleaseAfterCleanup();
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedFourSlots()
    {
        SchedulerExecutionGate gate = Ready();
        Task<SchedulerExecutionGate.Reservation?>[] tasks =
        [
            .. Enumerable.Range(0, 32).Select(_ => Task.Run(gate.TryBeginDispatch))
        ];
        SchedulerExecutionGate.Reservation?[] results = await Task.WhenAll(tasks);
        Assert.Equal(4, results.Count(result => result is not null));
        Assert.Equal(4, gate.ScheduledCount);
        Assert.Single(results, result => result?.AllowsSameProcess == true);
        foreach (SchedulerExecutionGate.Reservation? reservation in results)
        {
            reservation?.ReleaseAfterCleanup();
        }
    }

    private static SchedulerExecutionGate Ready()
    {
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        return gate;
    }
}
