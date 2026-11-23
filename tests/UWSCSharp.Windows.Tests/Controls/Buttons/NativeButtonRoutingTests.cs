using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonOperationTests
{
    [Theory]
    [InlineData(true, 43, 0, 0, 1)]
    [InlineData(false, 43, 0, 2, 0)]
    [InlineData(true, 43, 1, 0, 0)]
    [InlineData(true, 43, -1, 0, 1)]
    public void AccessibleByteControlsActionButActionFalseKeepsMatchSuccess(
        bool action, int role, int state, int posts, int actions)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        var platform = new Platform();
        using var accessible = new Accessible();
        accessible.States.Enqueue((state, role, action));
        Assert.True(NativeButtonControl.Click(fixture.Root, button,
            1, 1 | 512, platform, default, accessible));
        Assert.Equal(posts, platform.Posts.Count);
        Assert.Equal(actions, accessible.Actions);
        Assert.Equal(1, accessible.Gates);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RetryUsesUpdatedActionByte(bool first, bool second)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        var platform = new Platform();
        using var accessible = new Accessible();
        accessible.States.Enqueue((2, 44, first));
        accessible.States.Enqueue((0, 43, second));
        Assert.True(NativeButtonControl.Click(fixture.Root, button,
            1, 1, platform, default, accessible));
        Assert.Equal(2, accessible.Gates);
        Assert.Equal(1, accessible.Actions);
        Assert.Equal(2, platform.Posts.Count);
        Assert.Equal(second ? 2 : 1, platform.Events.Count(e => e == "activate"));
        Assert.Equal(40, platform.Events.Count(e => e == "pump"));
    }

    [Fact]
    public void RetryWithoutMarkerKeepsPreviousActionByte()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        var platform = new Platform();
        using var accessible = new Accessible();
        accessible.Marker.Enqueue(true);
        accessible.Marker.Enqueue(false);
        accessible.States.Enqueue((2, 44, true));
        Assert.True(NativeButtonControl.Click(fixture.Root, button,
            1, 1 | 512, platform, default, accessible));
        Assert.Equal(2, accessible.Gates);
        Assert.Equal(2, accessible.Actions);
        Assert.Empty(platform.Posts);
        Assert.DoesNotContain("activate", platform.Events);
    }

    [Fact]
    public void ExplicitMouseWinsOverAccessibleActionAndSameState()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        var platform = new Platform();
        using var accessible = new Accessible();
        accessible.States.Enqueue((1, 44, true));
        Assert.True(NativeButtonControl.Click(fixture.Root, button,
            1, 1 | 512 | 2048, platform, default, accessible));
        Assert.Equal(0, accessible.Actions);
        Assert.Equal(2, platform.Posts.Count);
        Assert.Equal(1, accessible.Gates);
    }

    [Fact]
    public void CancelledAccessibleActionPreventsRetry()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 3, 10);
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform();
        using var accessible = new Accessible { Acting = cancellation.Cancel };
        accessible.States.Enqueue((2, 44, true));
        Assert.Throws<OperationCanceledException>(() => NativeButtonControl.Click(
            fixture.Root, button, 1, 1 | 512, platform, cancellation.Token, accessible));
        Assert.Equal(1, accessible.Gates);
        Assert.DoesNotContain("sleep20", platform.Events);
    }

    [Fact]
    public async Task PublicCheckAndClickShareBackendUntilSessionDisposal()
    {
        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "target", 3, 10);
        var platform = new Platform();
        var accessible = new Accessible();
        accessible.States.Enqueue((2, 44, true));
        accessible.States.Enqueue((0, 43, true));
        int created = 0;
        await using (IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform,
            buttonAccessibleFactory: _ => { created++; return accessible; }).CreateSession())
        {
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("CHKBTN",
                [id, Value.String("target")], default));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(1 | 512)], default));
            Assert.Equal(1, created);
            Assert.Equal(0, accessible.Disposals);
            Assert.Equal(1, accessible.Actions);
            Assert.Empty(platform.Posts);
        }

        Assert.Equal(1, accessible.Disposals);
    }

    [Fact]
    public async Task UnusedSessionDoesNotCreateAccessibleBackend()
    {
        int created = 0;
        await using (IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(),
            buttonAccessibleFactory: _ => { created++; return new Accessible(); }).CreateSession())
        {
            Assert.Equal(0, created);
        }

        Assert.Equal(0, created);
    }

    private sealed class Accessible : IButtonAccessibleOperations
    {
        internal readonly Queue<bool> Marker = [];
        internal readonly Queue<(int State, int Role, bool Action)> States = [];
        internal int Gates;
        internal int Actions;
        internal int Disposals;
        internal Action? Acting;
        internal Action<int>? Gating;

        public bool Matches(nint root, CancellationToken token)
        {
            Gates++;
            Gating?.Invoke(Gates);
            return Marker.Count == 0 || Marker.Dequeue();
        }

        public int Read(nint root, nint target, CancellationToken token,
            ref int role, out bool accessibleAction)
        {
            (int state, int resultRole, bool action) = States.Dequeue();
            role = resultRole;
            accessibleAction = action;
            return state;
        }

        public bool Act(nint root, nint target, CancellationToken token)
        {
            Actions++;
            Acting?.Invoke();
            return false;
        }

        public void Dispose() => Disposals++;
    }
}
