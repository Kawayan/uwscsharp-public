using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class TrayLauncherClickStateTests
{
    [Fact]
    public void SingleClickWaitsForSystemIntervalAndFiresOnce()
    {
        var state = new TrayLauncherClickState();
        state.Click(100, 500);
        Assert.False(state.TakePending(599));
        Assert.True(state.TakePending(600));
        Assert.False(state.TakePending(601));
    }

    [Fact]
    public void DoubleClickCancelsPendingAndIgnoresFollowingButtonRelease()
    {
        var state = new TrayLauncherClickState();
        state.Click(100, 500);
        state.DoubleClick(200, 500);
        state.Click(201, 500);
        Assert.False(state.TakePending(701));
        state.Click(702, 500);
        Assert.True(state.TakePending(1202));
    }

    [Fact]
    public void ClosingOrHidingTheIconCancelsDelayedAction()
    {
        var state = new TrayLauncherClickState();
        state.Click(100, 500);
        state.Cancel();
        Assert.False(state.TakePending(1000));
        state.Click(1001, 500);
        Assert.True(state.TakePending(1501));
    }
}
