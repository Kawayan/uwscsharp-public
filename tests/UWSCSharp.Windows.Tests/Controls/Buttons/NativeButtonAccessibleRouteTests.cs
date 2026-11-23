using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeButtonAccessibleRouteTests
{
    [Fact]
    public void UnavailableReturnsMinusOneWithoutChangingRole()
    {
        var platform = new Platform { Loaded = false };
        int role = 17;
        Assert.Equal(-1, NativeButtonAccessibleRoute.Read(1, 2, platform,
            default, ref role, out bool action));
        Assert.False(action);
        Assert.Equal(17, role);
        Assert.Equal(new[] { "available" }, platform.Events);
    }

    [Fact]
    public void PointMatchReturnsEarlyAtTargetTopLeft()
    {
        var platform = new Platform { PointMatch = true };
        int role = 17;
        Assert.Equal(2, NativeButtonAccessibleRoute.Read(1, 2, platform,
            default, ref role, out bool action));
        Assert.False(action);
        Assert.Equal(44, role);
        Assert.Equal((10, 20), (platform.Point.X, platform.Point.Y));
        Assert.DoesNotContain("root:1", platform.Events);
        Assert.DoesNotContain("iconic:1", platform.Events);
    }

    [Theory]
    [InlineData(true, 44, 1)]
    [InlineData(false, 17, 0)]
    public void PointMissFallsBackToRootAndPreservesRoleOnMiss(
        bool match, int expectedRole, int expectedState)
    {
        var platform = new Platform { RootMatch = match };
        int role = 17;
        Assert.Equal(expectedState, NativeButtonAccessibleRoute.Read(1, 2,
            platform, default, ref role, out bool action));
        Assert.Equal(expectedRole, role);
        Assert.False(action);
        Assert.Contains("point", platform.Events);
        Assert.Contains("root:1", platform.Events);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(9, 1, false)]
    [InlineData(9, 9, true)]
    public void FallbackActionDependsOnForegroundAndPointWindow(
        int foreground, int pointWindow, bool expected)
    {
        var platform = new Platform { Front = foreground, Hit = pointWindow };
        int role = 0;
        NativeButtonAccessibleRoute.Read(1, 2, platform, default, ref role, out bool action);
        Assert.Equal(expected, action);
        Assert.Equal(foreground != 1, platform.Events.Contains("hit"));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 100, false)]
    [InlineData(-1, 0, true)]
    [InlineData(101, 0, true)]
    [InlineData(0, -1, true)]
    [InlineData(0, 101, true)]
    public void VirtualScreenIncludesRightAndBottomEdges(int x, int y, bool outside)
    {
        var platform = new Platform
        {
            Target = new Native.Rect { Left = x, Top = y }
        };
        int role = 0;
        NativeButtonAccessibleRoute.Read(1, 2, platform, default, ref role, out bool action);
        Assert.Equal(outside, action);
        Assert.Equal(!outside, platform.Events.Contains("point"));
        Assert.DoesNotContain("hit", platform.Events);
    }

    [Fact]
    public void MinimizedFallbackSelectsAccessibleActionWithoutPointWindowQuery()
    {
        var platform = new Platform { Front = 9, Minimized = 1 };
        int role = 0;
        NativeButtonAccessibleRoute.Read(1, 2, platform, default, ref role, out bool action);
        Assert.True(action);
        Assert.DoesNotContain("hit", platform.Events);
        Assert.DoesNotContain("metric:76", platform.Events);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void MdiUsesAncestorForForegroundButOriginalRootForMsaa(
        bool minimizedRoot, bool outsideAncestor, bool expected)
    {
        var platform = new Platform
        {
            Mdi = true,
            Front = 5,
            Minimized = minimizedRoot ? 1 : 0,
            Ancestor = new Native.Rect { Right = outsideAncestor ? 5 : 100, Bottom = 100 }
        };
        int role = 0;
        NativeButtonAccessibleRoute.Read(1, 2, platform, default, ref role, out bool action);
        Assert.Equal(expected, action);
        Assert.Equal(["parent:1", "parent:3", "parent:3", "parent:4", "parent:4"],
            platform.Events.Where(value => value.StartsWith("parent:")));
        Assert.Contains("root:1", platform.Events);
        Assert.Contains("bounds:5", platform.Events);
        Assert.DoesNotContain("root:5", platform.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActionUsesCheckedEndpointSumCenterAndReturnsProviderResult(bool result)
    {
        var platform = new Platform
        {
            ActionResult = result,
            Target = new Native.Rect { Left = -4, Right = -1, Top = -8, Bottom = -3 }
        };
        Assert.Equal(result, NativeButtonAccessibleRoute.Act(1, 2, platform, default));
        Assert.Equal((-2, -5), (platform.Point.X, platform.Point.Y));
        Assert.Equal(new[] { "bounds:2", "act:1" }, platform.Events);
    }

    [Fact]
    public void ActionCenterOverflowIsNotReplacedByDifferenceFormula()
    {
        var platform = new Platform
        {
            Target = new Native.Rect { Left = int.MaxValue - 1, Right = int.MaxValue }
        };
        Assert.Throws<OverflowException>(() => NativeButtonAccessibleRoute.Act(1, 2, platform, default));
        Assert.DoesNotContain("act:1", platform.Events);
    }

    [Fact]
    public void CancellationAfterRootReadDoesNotContinueGeometryChecks()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { OnRoot = cancellation.Cancel };
        int role = 0;
        Assert.Throws<OperationCanceledException>(() => NativeButtonAccessibleRoute.Read(
            1, 2, platform, cancellation.Token, ref role, out _));
        Assert.DoesNotContain("iconic:1", platform.Events);
    }

    private sealed class Platform : IButtonAccessibleRoutePlatform
    {
        internal bool Loaded = true;
        internal bool PointMatch;
        internal bool RootMatch;
        internal bool Mdi;
        internal bool ActionResult;
        internal nint Front = 1;
        internal nint Hit = 1;
        internal nint Minimized;
        internal Action? OnRoot;
        internal Native.Point Point;
        internal Native.Rect Target = new() { Left = 10, Top = 20, Right = 30, Bottom = 40 };
        internal Native.Rect Ancestor;
        internal readonly List<string> Events = [];

        public bool Available()
        {
            Events.Add("available");
            return Loaded;
        }

        public Native.Rect Bounds(nint window)
        {
            Events.Add("bounds:" + window);
            return window == 2 ? Target : Ancestor;
        }

        public int ExtendedStyle(nint window) => Mdi ? 0x40 : 0;

        public nint Parent(nint window)
        {
            Events.Add("parent:" + window);
            return window == 1 ? 3 : window is 3 or 4 ? window + 1 : 0;
        }

        public nint Foreground() => Front;

        public bool Iconic(nint window)
        {
            Events.Add("iconic:" + window);
            return window == Minimized;
        }

        public int Metric(int index)
        {
            Events.Add("metric:" + index);
            return index is 78 or 79 ? 100 : 0;
        }

        public nint PointWindow(Native.Point point, CancellationToken token)
        {
            Events.Add("hit");
            return Hit;
        }

        public bool ReadPoint(Native.Point point, ref int role, out int state,
            CancellationToken token)
        {
            Events.Add("point");
            Point = point;
            state = 0;
            if (PointMatch)
            {
                role = 44;
                state = 2;
            }

            return PointMatch;
        }

        public void ReadRoot(nint root, Native.Point point, ref int role, out int state,
            CancellationToken token)
        {
            Events.Add("root:" + root);
            Point = point;
            state = 0;
            if (RootMatch)
            {
                role = 44;
                state = 1;
            }

            OnRoot?.Invoke();
        }

        public bool ActRoot(nint root, Native.Point point, CancellationToken token)
        {
            Events.Add("act:" + root);
            Point = point;
            return ActionResult;
        }
    }
}
