using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonAccessibleApiTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void BackendCombinesWrapperWithRawSourceAndReleasesEachRead(bool front, int pointResult)
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(16) };
        using var variant = new Fixture();
        var source = new SessionSource(fixture, variant) { PointResult = pointResult };
        var pointWindows = new BackendPoints();
        var windows = new BackendWindows { Front = front ? 1 : 0 };
        using var backend = new NativeButtonAccessibleBackend(windows,
            new NativeButtonPointWindow(pointWindows), source, _ => 0, _ => 0);
        int role = 91;
        for (int count = 0; count < 2; count++)
        {
            Assert.Equal(1, backend.Read(1, 2, default, ref role, out bool action));
            Assert.Equal(44, role);
            Assert.False(action);
            Assert.Equal(1, fixture.References);
            Assert.Equal(1, variant.References);
        }

        Assert.Equal(front ? 0 : 1, pointWindows.Enumerations);
        Assert.Equal(front && pointResult == 0 ? 0 : 1, (int)source.Root);
    }

    [Fact]
    public void BackendDisposesFailedPointValuesWhenLaterGeometryThrows()
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var variant = new Fixture();
        var source = new SessionSource(fixture, variant) { PointResult = 1 };
        using var backend = new NativeButtonAccessibleBackend(
            new BackendWindows { FailIconic = true },
            new NativeButtonPointWindow(new BackendPoints()), source, _ => 0, _ => 0);
        int role = 91;
        Assert.Throws<InvalidOperationException>(() =>
            backend.Read(1, 2, default, ref role, out _));
        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
    }

    [Fact]
    public void BackendActionCancellationReleasesRoot()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Selected = cancellation.Cancel
        };
        using var variant = new Fixture();
        using var backend = new NativeButtonAccessibleBackend(new BackendWindows(),
            new NativeButtonPointWindow(new BackendPoints()),
            new SessionSource(fixture, variant), _ => 0, _ => 0);
        Assert.Throws<OperationCanceledException>(() => backend.Act(1, 2, cancellation.Token));
        Assert.Equal(1, fixture.References);
        Assert.DoesNotContain("action:0", fixture.Events);
    }

    [Fact]
    public void BackendReadsOwnedButtonThroughNativeLibraryAndWrapper()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "adapter", 3, 10);
        var windows = new BackendWindows
        {
            Front = 0,
            Rectangle = new OwnedListSelectionPlatform().Bounds(button)
        };
        using var backend = new NativeButtonAccessibleBackend(windows,
            new NativeButtonPointWindow(new BackendPoints()));
        int role = 91;
        Assert.Equal(0, backend.Read(button, button, default, ref role, out _));
        Assert.Equal(44, role);
    }

    [Fact]
    public void BackendUsesFreshSessionForActionAfterRead()
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var variant = new Fixture();
        var backend = new NativeButtonAccessibleBackend(new BackendWindows(),
            new NativeButtonPointWindow(new BackendPoints()),
            new SessionSource(fixture, variant), _ => 0, _ => 0);
        int role = 91;
        Assert.Equal(0, backend.Read(1, 2, default, ref role, out _));
        Assert.True(backend.Act(1, 2, default));
        Assert.Contains("action:0", fixture.Events);
        Assert.Equal(1, fixture.References);
        backend.Dispose();
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.Act(1, 2, default));
    }

    private sealed class BackendWindows : OwnedListSelectionPlatform
    {
        internal nint Front = 1;
        internal bool FailIconic;
        internal Native.Rect Rectangle = new() { Right = 10, Bottom = 10 };

        public override Native.Rect Bounds(nint window) => Rectangle;

        public override nint ForegroundWindow() => Front;

        public override bool IsIconic(nint window)
            => FailIconic ? throw new InvalidOperationException() : false;

        public override int Metric(int index) => index is 78 or 79 ? 100 : 0;

        public override void Sleep(int milliseconds, CancellationToken token)
            => token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token)
            => token.ThrowIfCancellationRequested();
    }

    private sealed class BackendPoints : IButtonPointWindowPlatform
    {
        internal int Enumerations;

        public double LocalDate() => 45000;

        public bool Enumerate(Func<nint, bool> visitor)
        {
            Enumerations++;
            return visitor(1);
        }

        public bool Visible(nint window) => true;

        public nint AtPoint(Native.Point point) => 1;

        public nint Parent(nint window) => 0;

        public Native.Rect Bounds(nint window) => default;
    }
}
