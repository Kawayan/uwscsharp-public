using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeButtonRouteGateTests
{
    [Theory]
    [InlineData(".NET-BroadcastEventWindow.1", true)]
    [InlineData("prefix.NET-BROADCASTEVENTWINDOWsuffix", true)]
    [InlineData("\u0001 .net-broadcasteventwindow \t", true)]
    [InlineData("NET-BROADCASTEVENTWINDOW", false)]
    [InlineData(".NET-BROADCASTEVENTWINDOＷ", false)]
    [InlineData("", false)]
    [InlineData("BUTTON", false)]
    public void UsesNormalizedSubstringOnRootThread(string label, bool expected)
    {
        var platform = new Platform { Labels = ["", label, "STATIC"] };
        Assert.Equal(expected, NativeButtonRouteGate.Matches(123, platform, default));
        Assert.Equal(123, platform.Root);
        Assert.Equal(42u, platform.EnumeratedThread);
        Assert.Equal(3, platform.Reads);
    }

    [Fact]
    public void DoesNotCacheMarkerBetweenCalls()
    {
        var platform = new Platform { Labels = [".NET-BROADCASTEVENTWINDOW"] };
        Assert.True(NativeButtonRouteGate.Matches(123, platform, default));
        platform.Labels = ["STATIC"];
        Assert.False(NativeButtonRouteGate.Matches(123, platform, default));
        Assert.Equal(2, platform.Enumerations);
    }

    [Fact]
    public void PropagatesLaterCollectorFailureEvenAfterMarker()
    {
        var error = new InvalidOperationException("fixture");
        var platform = new Platform
        {
            Labels = [".NET-BROADCASTEVENTWINDOW", "STATIC"],
            Failure = error
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            NativeButtonRouteGate.Matches(123, platform, default)));
        Assert.True(platform.Returned);
        Assert.Equal(2, platform.Reads);
    }

    [Fact]
    public void UsesCollectedLabelsWhenEnumerationReturnsFalse()
    {
        var platform = new Platform
        {
            Labels = [".NET-BROADCASTEVENTWINDOW"],
            Result = false
        };
        Assert.True(NativeButtonRouteGate.Matches(123, platform, default));
    }

    [Fact]
    public void PropagatesCancellationAfterReturningFromCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform
        {
            Labels = [".NET-BROADCASTEVENTWINDOW", "STATIC"],
            Read = cancellation.Cancel
        };
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeButtonRouteGate.Matches(123, platform, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(platform.Returned);
        Assert.Equal(1, platform.Reads);
    }

    private sealed class Platform : IButtonRouteGatePlatform
    {
        internal string[] Labels
        {
            get;
            set;
        } = [];

        internal bool Result
        {
            get;
            init;
        } = true;

        internal Exception? Failure
        {
            get;
            init;
        }

        internal Action? Read
        {
            get;
            init;
        }

        internal nint Root
        {
            get;
            private set;
        }

        internal uint EnumeratedThread
        {
            get;
            private set;
        }

        internal int Reads
        {
            get;
            private set;
        }

        internal int Enumerations
        {
            get;
            private set;
        }

        internal bool Returned
        {
            get;
            private set;
        }


        public uint Thread(nint root)
        {
            Root = root;
            return 42;
        }

        public bool Enumerate(uint thread, Func<nint, bool> visitor)
        {
            EnumeratedThread = thread;
            Enumerations++;
            for (int index = 0; index < Labels.Length; index++)
            {
                if (!visitor(index))
                {
                    break;
                }
            }

            Returned = true;
            return Result;
        }

        public string Label(nint window)
        {
            Reads++;
            Read?.Invoke();
            if (window == 1 && Failure is not null)
            {
                throw Failure;
            }

            return Labels[(int)window];
        }
    }
}
