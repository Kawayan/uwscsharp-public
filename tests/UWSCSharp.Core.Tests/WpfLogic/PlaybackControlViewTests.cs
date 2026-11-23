using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PlaybackControlViewTests
{
    [Fact]
    public void IdleDisablesActionsEvenWhenOldSignalsRemain()
    {
        var view = PlaybackControlView.Create(
            false, true, true, true);
        Assert.Equal(PlaybackControlPhase.Idle, view.Phase);
        Assert.False(view.IsActive);
        Assert.False(view.CanStop);
        Assert.False(view.CanPause);
    }

    [Fact]
    public void RunningOffersPauseAndStop()
    {
        var view = PlaybackControlView.Create(
            true, false, false, false);
        Assert.Equal(PlaybackControlPhase.Running, view.Phase);
        Assert.Equal("実行中", view.Status);
        Assert.Equal("Ⅱ 一時停止", view.PauseCaption);
        Assert.True(view.CanPause);
        Assert.True(view.CanStop);
    }

    [Fact]
    public void PendingPauseCanBeRevokedBeforeTheWorkerWaits()
    {
        var view = PlaybackControlView.Create(
            true, false, true, false);
        Assert.Equal(PlaybackControlPhase.PauseRequested, view.Phase);
        Assert.Equal("一時停止要求中", view.Status);
        Assert.Equal("▶ 再開", view.PauseCaption);
        Assert.True(view.CanPause);
        Assert.True(view.CanStop);
    }

    [Fact]
    public void ConfirmedPauseStillOffersStop()
    {
        var view = PlaybackControlView.Create(
            true, false, true, true);
        Assert.Equal(PlaybackControlPhase.Paused, view.Phase);
        Assert.Equal("一時停止中", view.Status);
        Assert.Equal("▶ 再開", view.PauseCaption);
        Assert.True(view.CanStop);
    }

    [Fact]
    public void ResumeIgnoresThePreviousWaitAcknowledgement()
    {
        var view = PlaybackControlView.Create(
            true, false, false, true);
        Assert.Equal(PlaybackControlPhase.Running, view.Phase);
        Assert.Equal("Ⅱ 一時停止", view.PauseCaption);
    }

    [Fact]
    public void StoppingRemainsVisibleAndWinsOverPause()
    {
        var view = PlaybackControlView.Create(
            true, true, true, true);
        Assert.Equal(PlaybackControlPhase.Stopping, view.Phase);
        Assert.Equal("停止を要求しました", view.Status);
        Assert.True(view.IsActive);
        Assert.False(view.CanStop);
        Assert.False(view.CanPause);
    }
}
