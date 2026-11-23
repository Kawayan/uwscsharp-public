using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class MainWindowActionStateTests
{
    [Fact]
    public void RecoveredIdleWindowAllowsPlaybackAndRecording()
    {
        MainWindowActionState state = Create();
        Assert.True(state.CanPlay);
        Assert.True(state.CanRecord);
        Assert.False(state.CanRetryCleanup);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void ActiveOperationBlocksAllThreeActions(
        bool running, bool starting, bool stopping, bool reserved)
    {
        var state = MainWindowActionState.Create(
            running, starting, stopping, reserved, false,
            false, true, true, true);
        Assert.Equal(new MainWindowActionState(false, false, false), state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingCanFinishWhileNewAdmissionsAreBlocked(bool lifetimeBusy)
    {
        MainWindowActionState state = Create(
            hasRecorder: true, canEnter: false, lifetimeBusy: lifetimeBusy,
            canRetry: true);
        Assert.False(state.CanPlay);
        Assert.True(state.CanRecord);
        Assert.False(state.CanRetryCleanup);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CleanupOrAdmissionRestrictionsPreventNewStarts(
        bool canEnter, bool lifetimeBusy)
    {
        MainWindowActionState state = Create(
            canEnter: canEnter, lifetimeBusy: lifetimeBusy);
        Assert.False(state.CanPlay);
        Assert.False(state.CanRecord);
    }

    [Fact]
    public void IdleUnrecoveredWindowOffersOnlyExplicitCleanup()
    {
        MainWindowActionState state = Create(canEnter: false, canRetry: true);
        Assert.Equal(new MainWindowActionState(false, false, true), state);
    }

    [Fact]
    public void RecordingControllerCanIndependentlyRejectRecordAction()
    {
        var state = MainWindowActionState.Create(
            false, false, false, false, false, false, false, true, false);
        Assert.True(state.CanPlay);
        Assert.False(state.CanRecord);
    }

    [Fact]
    public void CleanupCompletionRestoresActionsWithoutRetainingPreviousState()
    {
        Assert.False(Create(canEnter: false, canRetry: true).CanPlay);
        Assert.Equal(new MainWindowActionState(true, true, false), Create());
    }

    private static MainWindowActionState Create(
        bool hasRecorder = false, bool canEnter = true,
        bool lifetimeBusy = false, bool canRetry = false) =>
        MainWindowActionState.Create(
            false, false, false, false, lifetimeBusy,
            hasRecorder, true, canEnter, canRetry);
}
