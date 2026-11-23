using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeButtonPointWindowTests
{
    [Fact]
    public void ReusesVisibleTopLevelsAndRefreshesAfterExpiry()
    {
        var platform = new Platform();
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(1, platform.Enumerations);
        platform.TopLevels = [2];
        platform.Date += 0.5 / 86400;
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(1, platform.Enumerations);
        platform.Date += 0.6 / 86400;
        Assert.Equal(2, cache.Find(default, default));
        Assert.Equal(2, platform.Enumerations);
    }

    [Fact]
    public void SamplesTimeAfterEnumerationAndKeepsCacheOnClockRollback()
    {
        var platform = new Platform();
        platform.AfterEnumeration = () => platform.Date += 10d / 86400;
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(3, cache.Find(default, default));
        platform.AfterEnumeration = null;
        platform.TopLevels = [2];
        platform.Date += 0.5 / 86400;
        Assert.Equal(3, cache.Find(default, default));
        platform.Date -= 1;
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(1, platform.Enumerations);
    }

    [Theory]
    [InlineData(0.000011574074074074, 0)]
    [InlineData(0.000011574074074075, 1)]
    public void InitialTimestampUsesStrictLessThanThreshold(double now, int enumerations)
    {
        var platform = new Platform { Date = now, Start = 3 };
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(enumerations, platform.Enumerations);
    }

    [Fact]
    public void RoundedDoubleSecondRemainsBelowOriginalThreshold()
    {
        var platform = new Platform { Date = 1d / 86400, Start = 3 };
        var cache = new NativeButtonPointWindow(platform);
        cache.Find(default, default);
        Assert.Equal(0, platform.Enumerations);
    }

    [Theory]
    [InlineData(1.1574074074074073e-05, 0, true)]
    [InlineData(1.1574074074074075e-05, 0, false)]
    [InlineData(1.1574074074074075e-05, 8.999725064576941e-22, false)]
    [InlineData(1.1574074074074075e-05, 1.6940658945086007e-21, true)]
    public void FreshnessPreservesExtendedThresholdAndSubtractionResidual(
        double current, double previous, bool expected)
        => Assert.Equal(expected, NativeButtonPointWindow.IsFresh(current, previous));

    [Theory]
    [InlineData(694.25, true)]
    [InlineData(694.5, true)]
    [InlineData(694.75, false)]
    [InlineData(695, false)]
    public void FreshnessRoundsSubtractionToExtendedPrecision(double units, bool expected)
    {
        double current = Math.BitIncrement(1d / 86400d);
        double previous = Math.ScaleB(2048d - units, -80);
        Assert.Equal(expected, NativeButtonPointWindow.IsFresh(current, previous));
    }

    [Fact]
    public void FiltersHiddenWindowsAndSwallowsPerWindowFailures()
    {
        var platform = new Platform
        {
            TopLevels = [1, 2, 3],
            Visibility = window => window == 1
                ? false : window == 2 ? throw new InvalidOperationException() : true
        };
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(new nint[] { 1, 2, 3 }, platform.Visited);
    }

    [Fact]
    public void FailedEnumerationStillPublishesPartialCacheAndTimestamp()
    {
        var platform = new Platform { TopLevels = [2], EnumerationResult = false };
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(2, cache.Find(default, default));
        platform.TopLevels = [3];
        Assert.Equal(2, cache.Find(default, default));
        Assert.Equal(1, platform.Enumerations);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10, 0)]
    public void ZeroSizedParentStopsBeforePromotingIt(int width, int height)
    {
        var platform = new Platform { Width = width, Height = height };
        var cache = new NativeButtonPointWindow(platform);
        Assert.Equal(1, cache.Find(default, default));
        Assert.Equal(new nint[] { 2 }, platform.Measured);
    }

    [Fact]
    public void ReversedBoundsAreNotTreatedAsZeroSized()
    {
        var platform = new Platform { Width = -10, Height = -20 };
        Assert.Equal(3, new NativeButtonPointWindow(platform).Find(default, default));
    }

    [Fact]
    public void CachedTopLevelDoesNotAskForParentOrBounds()
    {
        var platform = new Platform { Start = 3 };
        Assert.Equal(3, new NativeButtonPointWindow(platform).Find(default, default));
        Assert.Empty(platform.Parents);
        Assert.Empty(platform.Measured);
    }

    [Fact]
    public void ParentWalkStopsAfterOneHundredPromotions()
    {
        var platform = new Platform { TopLevels = [], ParentProvider = window => window + 1 };
        Assert.Equal(101, new NativeButtonPointWindow(platform).Find(default, default));
        Assert.Equal(100, platform.Parents.Count);
        Assert.Equal(100, platform.Measured.Count);
    }

    [Fact]
    public void ZeroPointWindowStillUsesCacheAndParentQuery()
    {
        var platform = new Platform { Start = 0 };
        Assert.Equal(0, new NativeButtonPointWindow(platform).Find(default, default));
        Assert.Equal(1, platform.Enumerations);
        Assert.Equal(new nint[] { 0 }, platform.Parents);
    }

    [Fact]
    public void ResetTimestampForcesRefreshWithoutSharingInstances()
    {
        var platform = new Platform();
        var first = new NativeButtonPointWindow(platform);
        var second = new NativeButtonPointWindow(platform);
        first.Find(default, default);
        platform.TopLevels = [2];
        Assert.Equal(2, second.Find(default, default));
        Assert.Equal(3, first.Find(default, default));
        first.ResetTimestamp();
        Assert.Equal(2, first.Find(default, default));
        Assert.Equal(3, platform.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsCallbackWithoutCrossingNativeBoundary(bool throws)
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { TopLevels = [2, 3] };
        platform.Visibility = _ =>
        {
            cancellation.Cancel();
            if (throws)
            {
                throw new OperationCanceledException(cancellation.Token);
            }

            return true;
        };
        var cache = new NativeButtonPointWindow(platform);
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            cache.Find(default, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(platform.EnumerationReturned);
        Assert.Single(platform.Visited);
        platform.Visibility = _ => true;
        platform.TopLevels = [3];
        Assert.Equal(3, cache.Find(default, default));
        Assert.Equal(2, platform.Enumerations);
    }

    private sealed class Platform : IButtonPointWindowPlatform
    {
        internal double Date
        {
            get;
            set;
        } = 45000;

        internal nint Start
        {
            get;
            init;
        } = 1;

        internal nint[] TopLevels
        {
            get;
            set;
        } = [3];

        internal int Width
        {
            get;
            init;
        } = 10;

        internal int Height
        {
            get;
            init;
        } = 20;

        internal bool EnumerationResult
        {
            get;
            init;
        } = true;

        internal bool EnumerationReturned
        {
            get;
            private set;
        }

        internal int Enumerations
        {
            get;
            private set;
        }

        internal Action? AfterEnumeration
        {
            get;
            set;
        }

        internal Func<nint, bool> Visibility
        {
            get;
            set;
        } = _ => true;

        internal Func<nint, nint> ParentProvider
        {
            get;
            init;
        }

            = window => window is 1 or 2 ? window + 1 : 0;
        internal List<nint> Visited
        {
            get;
        } = [];

        internal List<nint> Parents
        {
            get;
        } = [];

        internal List<nint> Measured
        {
            get;
        } = [];


        public double LocalDate() => Date;

        public bool Enumerate(Func<nint, bool> visitor)
        {
            Enumerations++;
            foreach (nint window in TopLevels)
            {
                Visited.Add(window);
                if (!visitor(window))
                {
                    break;
                }
            }

            EnumerationReturned = true;
            AfterEnumeration?.Invoke();
            return EnumerationResult;
        }

        public bool Visible(nint window) => Visibility(window);

        public nint AtPoint(Native.Point point) => Start;

        public nint Parent(nint window)
        {
            Parents.Add(window);
            return ParentProvider(window);
        }

        public Native.Rect Bounds(nint window)
        {
            Measured.Add(window);
            return new Native.Rect { Right = Width, Bottom = Height };
        }
    }
}
